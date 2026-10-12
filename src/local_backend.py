from __future__ import annotations
from pathlib import Path
import sys
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "libs"))
import ctypes
from ctypes import wintypes
import hashlib
import ipaddress
import json
import time
from datetime import datetime, timedelta, timezone
import cbor2
from cryptography import x509
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from smartthings_local.protocol.auth import CertificateAuth
from smartthings_local.protocol.dtls_session import DtlsCoapSession
from smartthings_local.protocol.ocf_discovery import discover_ocf_secure_ports
from smartthings_local.protocol.dtls_probe import probe_dtls_port

PUBLIC_CLIENT_UUID = "ab0b0ac4-aae9-4958-a04d-8ec36fe1b2f9"
PREFIX = "x.com.samsung.da."
DATA = ROOT / "data"

class BrisaError(Exception):
    pass

class Blob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]

def protect_bytes(data: bytes, decrypt: bool = False) -> bytes:
    buffer = ctypes.create_string_buffer(data)
    incoming = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    outgoing = Blob()
    crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
    function = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
    function.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    function.restype = wintypes.BOOL
    if not function(ctypes.byref(incoming), None, None, None, None, 1, ctypes.byref(outgoing)):
        raise BrisaError("Não foi possível abrir a chave local deste usuário Windows.")
    try:
        return ctypes.string_at(outgoing.data, outgoing.size)
    finally:
        kernel32 = ctypes.WinDLL("kernel32")
        kernel32.LocalFree.argtypes = [ctypes.c_void_p]
        kernel32.LocalFree(outgoing.data)

def client_auth() -> CertificateAuth:
    DATA.mkdir(exist_ok=True)
    key_path = DATA / "client.dpapi"
    if key_path.exists():
        saved = json.loads(protect_bytes(key_path.read_bytes(), decrypt=True))
        return CertificateAuth.from_memory(saved["certificate"], saved["key"])
    private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([
        x509.NameAttribute(NameOID.ORGANIZATIONAL_UNIT_NAME, "uuid:" + PUBLIC_CLIENT_UUID),
        x509.NameAttribute(NameOID.COMMON_NAME, "urn:uuid:" + PUBLIC_CLIENT_UUID),
    ])
    now = datetime.now(timezone.utc)
    certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(private_key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=1)).not_valid_after(now + timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.CLIENT_AUTH]), critical=False)
        .add_extension(x509.SubjectAlternativeName([x509.UniformResourceIdentifier("urn:uuid:" + PUBLIC_CLIENT_UUID)]), critical=False)
        .sign(private_key, hashes.SHA256()))
    saved = {"certificate": certificate.public_bytes(serialization.Encoding.PEM).decode(),
        "key": private_key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()}
    encrypted = protect_bytes(json.dumps(saved).encode())
    temporary = key_path.with_suffix(".tmp")
    temporary.write_bytes(encrypted)
    temporary.replace(key_path)
    return CertificateAuth.from_memory(saved["certificate"], saved["key"])

def resource_map(body) -> dict:
    result = {}
    def visit(value):
        if isinstance(value, dict):
            if isinstance(value.get("href"), str) and isinstance(value.get("rep"), dict):
                result[value["href"]] = value["rep"]
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)
    visit(body)
    return result

def first(value):
    return value[0] if isinstance(value, list) and value else value

def normalize_state(resources: dict) -> dict:
    room = resources.get("/temperature/current/0", {})
    desired = resources.get("/temperature/desired/0", {})
    if not desired:
        combined = resources.get("/temperatures/vs/0", {})
        room = {"temperature": combined.get(PREFIX + "current"), "units": combined.get(PREFIX + "unit", "C")}
        desired = {"temperature": combined.get(PREFIX + "desired"), "units": combined.get(PREFIX + "unit", "C"), "range": combined.get(PREFIX + "range", [])}
    power = resources.get("/power/vs/0", {}).get(PREFIX + "power")
    if power is None and "/power/0" in resources:
        power = "On" if resources["/power/0"].get("value") else "Off"
    mode = resources.get("/mode/vs/0", {})
    fan = resources.get("/wind/strength/vs/0", {})
    convenient = resources.get("/mode/convenient/vs/0", {})
    convenience_modes = convenient.get(PREFIX + "supportedModes", [])
    current_convenience = convenient.get(PREFIX + "modes")
    direction = resources.get("/wind/direction/vs/0", {})
    bounds = desired.get("range", [])
    minimum = bounds[0] if isinstance(bounds, list) and len(bounds) == 2 else None
    maximum = bounds[1] if isinstance(bounds, list) and len(bounds) == 2 else None
    fan_values = fan.get(PREFIX + "supportedModes", [])
    fan_names = fan.get(PREFIX + "modesName", [])
    return {"power": power == "On" if power in ("On", "Off") else None,
        "room": room.get("temperature"), "setpoint": desired.get("temperature"),
        "unit": desired.get("units", "C"), "min": minimum, "max": maximum,
        "mode": first(mode.get(PREFIX + "modes")), "modes": mode.get(PREFIX + "supportedModes", []),
        "fan": str(first(fan.get(PREFIX + "modes"))) if fan.get(PREFIX + "modes") is not None else None,
        "fans": [{"value": str(value), "label": str(fan_names[i]) if i < len(fan_names) else str(value)} for i, value in enumerate(fan_values)],
        "windfree": current_convenience in ("Nano", "NanoSleep"),
        "windfree_supported": all(value in convenience_modes for value in ("Nano", "Off")),
        "sleep": current_convenience in ("Sleep", "NanoSleep"),
        "sleep_supported": all(value in convenience_modes for value in ("Sleep", "Off")),
        "convenience_modes": convenience_modes,
        "airflow": first(direction.get(PREFIX + "modes")),
        "airflows": direction.get(PREFIX + "supportedModes", [])}

def command_for(state: dict, resources: dict, control: str, value):
    if control == "temperature":
        if isinstance(value, bool) or not isinstance(value, (float, int)):
            raise BrisaError("Temperatura inválida.")
        if state.get("min") is None or state.get("max") is None or not state["min"] <= value <= state["max"]:
            raise BrisaError("Temperatura fora do intervalo permitido pelo aparelho.")
        if "/temperature/desired/0" not in resources:
            raise BrisaError("O aparelho não disponibilizou este controle de temperatura.")
        return "/temperature/desired/0", {"temperature": value}
    if control == "power" and isinstance(value, bool) and state.get("power") is not None:
        if "/power/vs/0" in resources:
            return "/power/vs/0", {PREFIX + "power": "On" if value else "Off"}
        return "/power/0", {"value": value}
    if control == "mode" and value in state.get("modes", []):
        return "/mode/vs/0", {PREFIX + "modes": [value]}
    if control == "fan" and value in [item["value"] for item in state.get("fans", [])]:
        return "/wind/strength/vs/0", {PREFIX + "modes": value}
    if control == "windfree" and isinstance(value, bool) and state.get("windfree_supported"):
        mode = ("NanoSleep" if value else "Sleep") if state.get("sleep") else ("Nano" if value else "Off")
        if mode in state.get("convenience_modes", []):
            return "/mode/convenient/vs/0", {PREFIX + "modes": mode}
    if control == "sleep" and isinstance(value, bool) and state.get("sleep_supported"):
        mode = ("NanoSleep" if value else "Nano") if state.get("windfree") else ("Sleep" if value else "Off")
        if mode in state.get("convenience_modes", []):
            return "/mode/convenient/vs/0", {PREFIX + "modes": mode}
    if control == "airflow" and isinstance(value, str) and value in state.get("airflows", []):
        return "/wind/direction/vs/0", {PREFIX + "modes": value}
    raise BrisaError("Este comando não está disponível no aparelho.")

def load_catalog():
    path = DATA / "devices.json"
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    config = json.loads((DATA / "device.json").read_text(encoding="utf-8"))
    config.update(id="office", identified=True)
    return {"version": 2, "selected_id": "office", "devices": [config]}

class LocalController:
    def __init__(self, device_id=None):
        catalog = load_catalog()
        selected = device_id or catalog["selected_id"]
        self.config = next((item for item in catalog["devices"] if item["id"] == selected), None)
        if self.config is None:
            raise BrisaError("Selecione um aparelho cadastrado.")
        address = ipaddress.ip_address(self.config["ip"])
        if address.version != 4 or not address.is_private or address.is_loopback:
            raise BrisaError("Configure um endereço IPv4 da rede local.")
        self.session = None
        self.auth = None
        self.next_connect = 0.0
        self.resources = {}
        self.last_state = None
        self.last_read = 0.0

    def connect(self):
        if self.session is not None:
            return
        if time.monotonic() < self.next_connect:
            raise BrisaError("Aguarde alguns segundos antes de reconectar ao aparelho.")
        self.next_connect = time.monotonic() + 15
        if self.auth is None:
            self.auth = client_auth()
        discovery = discover_ocf_secure_ports(self.config["ip"], discovery_port=self.config.get("plaintext_port", 49153), timeout=3, retries=0)
        if len(discovery.ports) != 1:
            raise BrisaError("O ar não respondeu. Verifique a rede Wi-Fi e o IP nas configurações.")
        port = discovery.ports[0]
        probe = probe_dtls_port(self.config["ip"], port, timeout=2, retries=0)
        if not probe.response_kind:
            raise BrisaError("A conexão local do ar está indisponível. Tente reconectar.")
        session = DtlsCoapSession(self.config["ip"], port, auth=self.auth, rate_limit_rps=2)
        try:
            session.connect(timeout=8)
            session.start_reader()
            code, body = session.get(["oic", "d"], timeout=5)
            if code != 69:
                raise BrisaError("O aparelho recusou a leitura local.")
            identity = cbor2.loads(body)
            identity_hash = hashlib.sha256(str(identity.get("di", "")).encode()).hexdigest()
            if identity_hash != self.config["identity_hash"]:
                raise BrisaError("O aparelho neste IP não é o ar selecionado. Confira o IP nas configurações.")
            types = identity.get("rt", [])
            if "oic.d.airconditioner" not in types:
                raise BrisaError("O aparelho não se identificou como ar-condicionado.")
            self.session = session
        except Exception:
            session.close()
            raise

    def close(self):
        if self.session is not None:
            session, self.session = self.session, None
            session.close()

    def read(self):
        self.connect()
        try:
            code, body = self.session.get(["device", "0"], query=("if=oic.if.b",), timeout=8)
            if code != 69:
                raise BrisaError("Não foi possível ler o estado do ar selecionado.")
            self.resources = resource_map(cbor2.loads(body))
            if not self.resources:
                raise BrisaError("O ar retornou uma resposta sem controles.")
            state = normalize_state(self.resources)
            state["name"] = self.config.get("name", "Escritório")
            state["device_id"] = self.config["id"]
            self.last_state = state
            self.last_read = time.monotonic()
            return state
        except Exception:
            self.close()
            raise

    def write(self, control, value):
        before = self.last_state if control not in ("windfree", "sleep") and self.session is not None and self.last_state is not None and time.monotonic() - self.last_read < 30 else self.read()
        href, body = command_for(before, self.resources, control, value)
        code, _ = self.session.post(href.strip("/").split("/"), cbor2.dumps(body), timeout=8)
        if not 64 <= code < 96:
            raise BrisaError("O ar recusou o comando. Atualize o estado e tente novamente.")
        after = self.read()
        key = {"temperature": "setpoint"}.get(control, control)
        if after.get(key) != value:
            after = self.read()
        if after.get(key) != value:
            raise BrisaError("O comando foi recebido, mas a alteração ainda não foi confirmada. Atualize o estado.")
        return after

def main():
    controller = None
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    try:
        for line in sys.stdin:
            request = {}
            try:
                if len(line) > 4096:
                    raise BrisaError("Solicitação inválida.")
                request = json.loads(line)
                if request.get("op") == "shutdown":
                    break
                if request.get("op") == "select":
                    if controller:
                        controller.close()
                    controller = None
                    controller = LocalController(request.get("value"))
                    state = controller.read()
                elif request.get("op") == "state":
                    if controller is None:
                        controller = LocalController()
                    state = controller.read()
                elif request.get("op") == "set":
                    if controller is None:
                        controller = LocalController()
                    state = controller.write(request.get("control"), request.get("value"))
                else:
                    raise BrisaError("Solicitação inválida.")
                result = {"id": request.get("id"), "ok": True, "state": state}
            except BrisaError as error:
                result = {"id": request.get("id"), "ok": False, "error": str(error)}
            except Exception:
                if controller:
                    controller.close()
                result = {"id": request.get("id"), "ok": False, "error": "A conexão local falhou. Verifique o Wi-Fi e clique em Reconectar."}
            print(json.dumps(result, ensure_ascii=False), flush=True)
    finally:
        if controller:
            controller.close()

if __name__ == "__main__":
    main()
