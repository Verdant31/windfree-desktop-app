import argparse
import hashlib
import ipaddress
import json
import re
import uuid

from local_backend import (
    DATA, BrisaError, DtlsCoapSession, cbor2, client_auth,
    discover_ocf_secure_ports, probe_dtls_port,
)

PRIVATE_NETWORKS = tuple(ipaddress.ip_network(value) for value in (
    "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
))


def validate_device(name, ip, device_id):
    name = name.strip()
    if not name or len(name) > 28 or any(ord(char) < 32 for char in name):
        raise BrisaError("Use um nome de cômodo com 1 a 28 caracteres.")
    try:
        address = ipaddress.ip_address(ip)
    except ValueError:
        raise BrisaError("Informe um IPv4 da rede local.") from None
    if not isinstance(address, ipaddress.IPv4Address) or not any(address in network for network in PRIVATE_NETWORKS):
        raise BrisaError("Informe um IPv4 privado da rede local.")
    if device_id is not None and not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,63}", device_id):
        raise BrisaError("O identificador aceita letras minúsculas, números, hífen e sublinhado.")
    return name, str(address)


def read_identity(ip, discovery_port):
    discovery = discover_ocf_secure_ports(ip, discovery_port=discovery_port, timeout=3, retries=0)
    if len(discovery.ports) != 1:
        raise BrisaError("O aparelho não respondeu à descoberta local. Confira o IP e a rede Wi-Fi.")
    port = discovery.ports[0]
    if not probe_dtls_port(ip, port, timeout=2, retries=0).response_kind:
        raise BrisaError("O aparelho não disponibilizou uma conexão DTLS.")
    session = DtlsCoapSession(ip, port, auth=client_auth(), rate_limit_rps=2)
    try:
        session.connect(timeout=8)
        session.start_reader()
        code, body = session.get(["oic", "d"], timeout=5)
        if code != 69:
            raise BrisaError("O aparelho recusou a leitura da identidade.")
        identity = cbor2.loads(body)
        if not isinstance(identity, dict) or not isinstance(identity.get("di"), str) or not identity["di"] or "oic.d.airconditioner" not in identity.get("rt", []):
            raise BrisaError("O dispositivo não se identificou como ar-condicionado.")
        return hashlib.sha256(identity["di"].encode()).hexdigest()
    finally:
        session.close()


def save_device(name, ip, device_id, discovery_port, identity_hash):
    DATA.mkdir(exist_ok=True)
    path = DATA / "devices.json"
    catalog = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {
        "version": 2, "selected_id": None, "devices": [],
    }
    devices = catalog["devices"]
    existing = next((item for item in devices if
        (device_id is not None and item["id"] == device_id) or
        (device_id is None and (item["name"] == name or item["identity_hash"] == identity_hash))), None)
    device = existing if existing is not None else {}
    device.update({
        "id": device_id or device.get("id") or uuid.uuid4().hex,
        "name": name, "ip": ip, "plaintext_port": discovery_port,
        "identity_hash": identity_hash, "identified": True,
    })
    if existing is None:
        devices.append(device)
    if not any(item["id"] == catalog.get("selected_id") for item in devices):
        catalog["selected_id"] = device["id"]
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def main():
    parser = argparse.ArgumentParser(description="Cadastra um ar Samsung pela identidade lida na rede local.")
    parser.add_argument("--name", required=True)
    parser.add_argument("--ip", required=True)
    parser.add_argument("--id")
    parser.add_argument("--discovery-port", type=int, default=49153)
    args = parser.parse_args()
    try:
        name, ip = validate_device(args.name, args.ip, args.id)
        if not 1 <= args.discovery_port <= 65535:
            raise BrisaError("Porta de descoberta inválida.")
        identity_hash = read_identity(ip, args.discovery_port)
        save_device(name, ip, args.id, args.discovery_port, identity_hash)
        print(f"{name} cadastrado. Você já pode abrir o Brisa.")
    except BrisaError as error:
        parser.exit(1, str(error) + "\n")
    except Exception:
        parser.exit(1, "Não foi possível cadastrar o aparelho. Confira a conexão local e tente novamente.\n")


if __name__ == "__main__":
    main()
