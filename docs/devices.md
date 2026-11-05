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

## Mixed device groups and notifications

Edit groups in a client's expanded Devices panel, or send `PUT /api/devices/clients/{clientId}/groups` with `{"groups":["Upstairs","Office"]}`. Up to 32 group names of 64 characters each are supported; names are trimmed and deduplicated without regard to case. Client self-registration cannot overwrite these assignments.

Satellites use the **Groups** tab in their Devices details. Choose the configured client owner and enter the same group names as the Windows and Android clients. For example, the `Upstairs` group for owner `gary` can contain a bedroom EchoMuse, an office Windows desktop and an Android phone. The shared group overview lists all members, including offline devices.

`PUT /api/devices/satellites/{satelliteId}/groups` accepts `{"owner":"gary","groups":["Upstairs"]}`. An empty list removes this owner's memberships for that satellite. `GET /api/devices/groups` returns the available owners and satellite assignments. Membership is persisted in `notifications.db`, retained across provider reconnects and removed when the satellite registration is deleted. Satellite hardware names and serial numbers do not grant group access. Different owners can reuse a group name without sharing recipients.

The existing `POST /api/client/notifications` API accepts either `targetClientId` for one client or `targetGroup` for a group. For example:

```json
{
  "kind": "announcement",
  "text": "Dinner is ready",
  "idempotencyKey": "dinner-2026-10-25",
  "targetGroup": "Upstairs"
}
```

Groups also apply to timer, alarm, reminder and notification kinds. Membership is evaluated at delivery time within the notification owner's client and satellite assignments. Offline clients retrieve unexpired notifications when they reconnect. Offline, busy or unsupported satellites remain pending until they can accept playback or the notice expires. With neither target supplied, existing owner-wide client delivery is preserved; satellites only receive explicitly group-targeted notifications.

Examples of supported commands:

- `announce to group Upstairs: Dinner is ready`
- `announce alarm to group Upstairs: Wake up`
- `set a tea timer for 5 minutes on group Upstairs`

A rich-client conversation can target its owner's groups. A satellite can originate group announcements and timers only for groups it belongs to; if the same group name grants it access under multiple owners, use a rich-client conversation to disambiguate. Timers persist their owner and group alongside the timer, and expiry creates one durable group notification with alarm importance. Timer cancellation and remaining-time queries use the original device or conversation as before.

For a scheduled alarm, send a group notification with `kind: "alarm"`, `importance: "alarm"` and an ISO-8601 `dueAt`. The same schedule reaches clients and satellites. Windows and Android presentation and acknowledgement remain client responsibilities. Satellite delivery uses the existing TTS, volume, tones, alarm repetition and local acknowledgement behavior. Acknowledging one satellite or client does not dismiss other group members.

Successful satellite delivery is recorded per notification and satellite, so service restarts do not replay already delivered notices. Delivery is at least once: a crash after playback but before recording success may repeat it. Existing in-memory alarm repetition does not resume across a server restart. Undelivered schedules, group assignments and delivery records persist. Cancellation, expiry or removal of satellite membership clears the matching repeating group announcement without stopping unrelated playback or voice sessions.

Native Windows and Android application implementations live outside this repository; they must send the optional fields to populate hardware and installed-version details.
