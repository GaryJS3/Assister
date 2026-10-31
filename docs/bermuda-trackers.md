# Bermuda tracker location

Ask “where are Gary's keys?”, “where is Meg's phone?”, “find Gary's keys”, or “locate Meg's phone”. The built-in Locate Bermuda tracker intent is read-only and can be given additional phrases in the intent workbench. The AI also has the read-only `ha_locate_tracker` tool for location questions and conversational requests.

Trackers come from the existing Home Assistant connection. Matching uses Bermuda Area sensors, device names, entity names, aliases, or exact entity IDs; apostrophes in possessives are optional. Give each tracker a distinct device name or alias. Multiple matches require clarification, regardless of the requesting satellite's room.

Last seen location is included only when no current location is available, in both direct responses and AI tool results. The current location is the Area sensor's state, never its assigned Home Assistant area. The Area Last Seen sensor is paired using its registry device ID, so renamed entity IDs work. Enable Bermuda's optional Area Last Seen entity for each tracked device to retain its last area. Missing or unavailable readings are reported explicitly.

Responses say “Last updated about 5 minutes ago” using Home Assistant's `last_updated` timestamp from the sensor supplying the location: Area when the current location is known, or Area Last Seen when falling back to the retained location. Tool results expose `LastUpdatedAt` and `LastUpdatedSecondsAgo`. Missing, invalid or future update timestamps are reported as unavailable. This is a sensor update time, not a Bluetooth detection time; `last_changed` and `last_seen` are not used. Sensor unavailability is reported separately from loss of area visibility. A disconnected Home Assistant cache is not used to report a current location.

Upstream entity contracts: [Bermuda sensors](https://github.com/agittins/bermuda/blob/main/custom_components/bermuda/sensor.py) and [device trackers](https://github.com/agittins/bermuda/blob/main/custom_components/bermuda/device_tracker.py).
