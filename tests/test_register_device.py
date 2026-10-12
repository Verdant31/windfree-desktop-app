from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch
import hashlib
import json
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import register_device as register


class RegistrationTests(unittest.TestCase):
    def test_accepts_private_ipv4_and_trims_room_name(self):
        for ip in ("10.1.2.3", "172.16.0.10", "192.168.1.10"):
            self.assertEqual(register.validate_device(" Quarto ", ip, "bedroom"), ("Quarto", ip))

    def test_rejects_external_and_invalid_addresses(self):
        for ip in ("8.8.8.8", "127.0.0.1", "169.254.1.1", "224.0.0.1", "::1", "localhost", "invalid"):
            with self.subTest(ip=ip), self.assertRaises(register.BrisaError):
                register.validate_device("Quarto", ip, None)

    def test_rejects_invalid_names_and_ids(self):
        for name, device_id in ((" ", None), ("x" * 29, None), ("a\nb", None), ("Quarto", "../room")):
            with self.subTest(name=name, device_id=device_id), self.assertRaises(register.BrisaError):
                register.validate_device(name, "192.168.1.10", device_id)

    def test_registration_reads_identity_and_closes_session(self):
        session = Mock()
        identity = {"di": "test-appliance", "rt": ["oic.d.airconditioner"]}
        session.get.return_value = (69, register.cbor2.dumps(identity))
        with patch.object(register, "discover_ocf_secure_ports", return_value=SimpleNamespace(ports=[49154])), \
             patch.object(register, "probe_dtls_port", return_value=SimpleNamespace(response_kind="hello")), \
             patch.object(register, "client_auth", return_value=object()), \
             patch.object(register, "DtlsCoapSession", return_value=session):
            result = register.read_identity("192.168.1.10", 49153)
        self.assertEqual(result, hashlib.sha256(b"test-appliance").hexdigest())
        session.get.assert_called_once_with(["oic", "d"], timeout=5)
        session.post.assert_not_called()
        session.close.assert_called_once()

    def test_rejected_device_closes_session(self):
        session = Mock()
        session.get.return_value = (69, register.cbor2.dumps({"di": "test", "rt": ["oic.d.light"]}))
        with patch.object(register, "discover_ocf_secure_ports", return_value=SimpleNamespace(ports=[49154])), \
             patch.object(register, "probe_dtls_port", return_value=SimpleNamespace(response_kind="hello")), \
             patch.object(register, "client_auth", return_value=object()), \
             patch.object(register, "DtlsCoapSession", return_value=session):
            with self.assertRaises(register.BrisaError):
                register.read_identity("192.168.1.10", 49153)
        session.close.assert_called_once()
        session.post.assert_not_called()

    def test_catalog_updates_room_without_losing_selection(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(register, "DATA", Path(temporary)):
            register.save_device("Quarto", "192.168.1.10", "bedroom", 49153, "a" * 64)
            register.save_device("Gourmet", "192.168.1.11", "patio", 49153, "b" * 64)
            register.save_device("Quarto", "192.168.1.12", None, 49153, "a" * 64)
            catalog = json.loads((Path(temporary) / "devices.json").read_text(encoding="utf-8"))
            self.assertEqual(catalog["selected_id"], "bedroom")
            self.assertEqual(len(catalog["devices"]), 2)
            self.assertEqual(catalog["devices"][0]["ip"], "192.168.1.12")
            self.assertEqual(catalog["devices"][0]["id"], "bedroom")
            self.assertFalse((Path(temporary) / "devices.tmp").exists())


if __name__ == "__main__":
    unittest.main()
