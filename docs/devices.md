# Device inventory

The management navigation opens `/devices.html`. `/satellites.html` remains compatible with existing bookmarks and satellite detail links. Audio-only endpoints (Home Assistant Voice, EchoMuse dots, etc.) appear under Satellites with their existing controls. Windows and Android registrations have separate sections; web and other client types appear under Other clients.

## Client registration

Use one configured authenticated client identity per physical client installation. The server derives the stable client ID and owner from the bearer token; device names and serial numbers are descriptive metadata, never identity or authorization. Sharing a token between physical devices replaces the same inventory entry.

`POST /api/client/clients/register` accepts the existing registration payload plus optional version and hardware fields:

```json
{
  "clientType": "windows",
  "deviceName": "Office desktop",
  "capabilities": ["clipboard.write"],
  "clientVersion": "2.0.1",
  "hardware": {
    "operatingSystemVersion": "Windows 11 25H2",
    "manufacturer": "Example",
    "model": "Desktop",
    "serialNumber": "optional-serial"
  }
}
```

Use `clientType: "android"` for Android devices. Hardware fields are independently optional and self-reported. Client version is limited to 128 characters; each hardware field to 256. Omitting the client version or hardware object retains the previous value. Existing three-field registrations remain valid.

Client inventory is stored in `RegisteredDevices` in `client-signals.db` under `Assister:DataPath`. Re-registration updates the entry while preserving the first registration timestamp and management-assigned groups. Disconnect retains the entry and marks it offline. After a server restart, all restored entries start offline. The existing signal stream refreshes presence; 90 seconds without a heartbeat marks a client offline. Last-seen timestamps are checkpointed at most every 15 seconds during streaming.

`GET /api/devices/clients` returns the management inventory, including metadata, first registration, last seen, groups and presence. Like the existing satellite management API, it uses the deployment's management access boundary. `GET /api/client/clients` remains restricted to the authenticated owner.

## Client groups and notifications

Edit groups in a client's expanded Devices panel, or send `PUT /api/devices/clients/{clientId}/groups` with `{"groups":["Upstairs","Office"]}`. Up to 32 group names of 64 characters each are supported; names are trimmed and deduplicated without regard to case. Client self-registration cannot overwrite these assignments.

The existing `POST /api/client/notifications` API accepts either `targetClientId` for one client or `targetGroup` for a group. For example:

```json
{
  "kind": "announcement",
  "text": "Dinner is ready",
  "idempotencyKey": "dinner-2026-10-25",
  "targetGroup": "Upstairs"
}
```

Groups also apply to timer, alarm, reminder and notification kinds. Membership is evaluated at delivery time and always within the notification owner's clients. Offline clients can retrieve unexpired notifications when they reconnect. With neither target supplied, existing owner-wide delivery is preserved. Notifications use existing durable storage, scheduling, WebSocket events and receipts; actual display, playback and acknowledgement remain client responsibilities.

These groups currently target rich clients. Satellite announcement routing and existing satellite controls retain their current behavior. Native Windows and Android application implementations live outside this repository; they must send the optional fields to populate hardware and installed-version details.
