// Observe entry points only. Never enumerate devices, request permissions, connect or transfer data.
function collectHardwareDevicesObservation(target = globalThis) {
  try {
    const navigator = target.navigator;
    if (!navigator || typeof navigator !== 'object') return {status:'NotPerformed'};
    const navigatorApis = Object.fromEntries(['bluetooth','usb','hid','serial'].map(name => [name,name in navigator]));
    const constructors = Object.fromEntries(['Bluetooth','BluetoothDevice','BluetoothUUID','BluetoothRemoteGATTServer',
      'BluetoothRemoteGATTService','BluetoothRemoteGATTCharacteristic','BluetoothRemoteGATTDescriptor','BluetoothAdvertisingEvent',
      'USB','USBDevice','USBConnectionEvent','USBInTransferResult','USBOutTransferResult','USBIsochronousInTransferPacket',
      'USBIsochronousInTransferResult','USBIsochronousOutTransferPacket','USBIsochronousOutTransferResult',
      'HID','HIDDevice','HIDConnectionEvent','HIDInputReportEvent','Serial','SerialPort'].map(name => [name,name in target]));
    return {status:'Observed',secureContext:target.isSecureContext === true,
      documentContext:typeof target.document === 'object',navigatorApis,constructors};
  } catch { return {status:'NotPerformed'}; }
}

function hardwareDevicesObservationOutcome(observation, worker = false) {
  if (!observation || observation.status !== 'Observed' || observation.secureContext !== true
      || observation.documentContext !== !worker) return 'Unavailable';
  const groups = {navigatorApis:['bluetooth','usb','hid','serial'],constructors:['Bluetooth','BluetoothDevice','BluetoothUUID',
    'BluetoothRemoteGATTServer','BluetoothRemoteGATTService','BluetoothRemoteGATTCharacteristic','BluetoothRemoteGATTDescriptor',
    'BluetoothAdvertisingEvent','USB','USBDevice','USBConnectionEvent','USBInTransferResult','USBOutTransferResult',
    'USBIsochronousInTransferPacket','USBIsochronousInTransferResult','USBIsochronousOutTransferPacket','USBIsochronousOutTransferResult',
    'HID','HIDDevice','HIDConnectionEvent','HIDInputReportEvent','Serial','SerialPort']};
  let missing = false;
  for (const [group,names] of Object.entries(groups)) {
    const observed = observation[group];
    if (!observed || typeof observed !== 'object' || Array.isArray(observed)) {missing = true;continue;}
    for (const name of names) {
      if (observed[name] === true) return 'Violation';
      if (observed[name] !== false) missing = true;
    }
  }
  return missing ? 'Unavailable' : 'Verified';
}
