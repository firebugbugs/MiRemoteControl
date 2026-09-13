'use strict';

// This follows the hardware-validated Windows RC003 tap in
// miaomiaozii/windows-remote-mic-app. HidOverGatt returns a nine-byte buffer:
// 01 00 00 followed by three little-endian HID usages. Windows drops Back
// (00f1) and the two volume usages before they reach Raw Input, so observe the
// completed read in the RC003 WUDFHost instead.
const READ_CHARACTERISTIC_IOCTL = 0x80018483;
const EXPECTED_OUTPUT_LENGTH = 9;
const TV_USAGE = 0x0035;
const HEARTBEAT_INTERVAL_MS = 5000;
const RECONNECT_DELAY_MS = 1000;

let host = '127.0.0.1';
let port = 30684;
let output = null;
let writeChain = Promise.resolve();
let reconnectTimer = null;
let hookInstalled = false;

function asciiBytes(text) {
  const result = [];
  for (let index = 0; index < text.length; index++) {
    result.push(text.charCodeAt(index) & 0xff);
  }
  return result;
}

function hex(pointer, length) {
  if (pointer.isNull() || length <= 0) return '';
  const bytes = new Uint8Array(pointer.readByteArray(length));
  let result = '';
  for (let index = 0; index < bytes.length; index++) {
    result += bytes[index].toString(16).padStart(2, '0');
  }
  return result;
}

function hasUsage(pointer, usage) {
  // RC003 report: report-id, reserved word, then three LE HID usages.
  if (pointer.isNull() || pointer.readU8() !== 1) return false;
  for (let offset = 3; offset < EXPECTED_OUTPUT_LENGTH; offset += 2) {
    if (pointer.add(offset).readU16() === usage) return true;
  }
  return false;
}

function clearUsage(pointer, usage) {
  for (let offset = 3; offset < EXPECTED_OUTPUT_LENGTH; offset += 2) {
    const slot = pointer.add(offset);
    if (slot.readU16() === usage) slot.writeU16(0);
  }
}

function scheduleReconnect() {
  if (reconnectTimer !== null) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectToHost();
  }, RECONNECT_DELAY_MS);
}

function markDisconnected(currentOutput) {
  if (output !== currentOutput) return;
  output = null;
  scheduleReconnect();
}

function emit(payload) {
  const currentOutput = output;
  if (currentOutput === null) {
    scheduleReconnect();
    return;
  }
  const line = JSON.stringify(payload) + '\n';
  writeChain = writeChain
    .then(() => currentOutput.writeAll(asciiBytes(line)))
    .catch(() => markDisconnected(currentOutput));
}

async function connectToHost() {
  if (output !== null) return;
  try {
    const connection = await Socket.connect({
      family: 'ipv4',
      host: host,
      port: port
    });
    output = connection.output;
    emit({ kind: 'ready', pid: Process.id, hook_installed: hookInstalled });
  } catch (_) {
    output = null;
    scheduleReconnect();
  }
}

function installHook() {
  if (hookInstalled) return;
  const ntdll = Process.findModuleByName('ntdll.dll');
  const target = ntdll ? ntdll.findExportByName('NtDeviceIoControlFile') : null;
  if (target === null) {
    emit({ kind: 'error', message: 'NtDeviceIoControlFile export not found' });
    return;
  }

  Interceptor.attach(target, {
    onEnter(args) {
      this.ioctl = args[5].toUInt32();
      this.outputLength = args[9].toUInt32();
      // Back/volume arrive on READ_CHARACTERISTIC_IOCTL, while the standard
      // keyboard usage used by TV can be completed through another HID path.
      // Inspect every RC003-shaped output so usage 0x35 can be removed before
      // WUDFHost hands it to Windows' keyboard mapper.
      this.capture = this.outputLength === EXPECTED_OUTPUT_LENGTH;
      if (this.capture) {
        this.output = args[8];
      }
    },
    onLeave(result) {
      if (!this.capture || result.toUInt32() !== 0 || this.output.isNull()) return;
      try {
        const tv = hasUsage(this.output, TV_USAGE);
        if (tv || this.ioctl === READ_CHARACTERISTIC_IOCTL) {
          // Copy the original bytes first: MiRemoteControl still needs the TV
          // event to toggle the mirror even though Windows must not type it.
          emit({ kind: 'gatt_read', raw: hex(this.output, this.outputLength) });
        }
        if (tv) clearUsage(this.output, TV_USAGE);
      } catch (error) {
        emit({ kind: 'error', message: String(error) });
      }
    }
  });
  hookInstalled = true;
}

setInterval(() => {
  if (output === null) scheduleReconnect();
  else emit({ kind: 'heartbeat', pid: Process.id });
}, HEARTBEAT_INTERVAL_MS);

rpc.exports = {
  async init(_stage, parameters) {
    host = parameters.host || host;
    port = parameters.port || port;
    installHook();
    await connectToHost();
  }
};
