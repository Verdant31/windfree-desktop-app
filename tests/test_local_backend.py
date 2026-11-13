from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import local_backend as backend

P = backend.PREFIX


def vendor_resources():
    return {"/temperatures/vs/0": {P + "items": [{
        P + "id": "0", P + "description": "Temperature",
        P + "desired": "22.0", P + "current": "26.0",
        P + "minimum": "16", P + "maximum": "30", P + "unit": "Celsius",
    }]}}


class TemperatureTests(unittest.TestCase):
    def test_reads_item_temperatures_without_standard_resources(self):
        state = backend.normalize_state(vendor_resources())
        self.assertEqual((state['setpoint'], state['room'], state['min'], state['max'], state['unit']),
                         (22.0, 26.0, 16.0, 30.0, 'C'))

    def test_uses_vendor_write_even_when_standard_endpoint_is_advertised(self):
        resources = vendor_resources()
        resources['/temperature/desired/0'] = {'temperature': 22.0, 'range': [16, 30]}
        href, body = backend.command_for(backend.normalize_state(resources), resources, 'temperature', 23)
        self.assertEqual(href, '/temperatures/vs/0')
        self.assertEqual(body, {P + 'items': [{P + 'id': '0', P + 'desired': '23.0'}]})

    def test_preserves_standard_only_device_support(self):
        resources = {'/temperature/desired/0': {'temperature': 22.0, 'range': [16, 30]}}
        self.assertEqual(backend.command_for(backend.normalize_state(resources), resources, 'temperature', 23),
                         ('/temperature/desired/0', {'temperature': 23}))

    def test_rejects_temperature_outside_device_range(self):
        resources = vendor_resources()
        with self.assertRaises(backend.BrisaError):
            backend.command_for(backend.normalize_state(resources), resources, 'temperature', 31)

    def controller(self):
        controller = object.__new__(backend.LocalController)
        controller.session = Mock()
        controller.session.post.return_value = (68, b'')
        controller.resources = vendor_resources()
        controller.last_state = backend.normalize_state(controller.resources)
        controller.last_read = backend.time.monotonic()
        return controller

    @patch.object(backend.time, 'sleep')
    def test_unconfirmed_command_returns_current_state_without_disconnect(self, sleep):
        controller = self.controller()
        controller.read = Mock(side_effect=[{'setpoint': 22.0}, {'setpoint': 22.0}])
        state = controller.write('temperature', 23)
        self.assertEqual(state['setpoint'], 22.0)
        self.assertIn('warning', state)
        controller.session.close.assert_not_called()
        self.assertEqual(controller.session.post.call_count, 1)

    @patch.object(backend.time, 'sleep')
    def test_delayed_confirmation_is_successful(self, sleep):
        controller = self.controller()
        controller.read = Mock(side_effect=[{'setpoint': 22.0}, {'setpoint': 23.0}])
        self.assertEqual(controller.write('temperature', 23), {'setpoint': 23.0})


if __name__ == '__main__':
    unittest.main()
