# Google Calendar

Open **Calendars** in the dashboard (`/calendars.html`). Enable the Google Calendar API in your Google Cloud project, create a service account and download its JSON key. Share each existing calendar with the key's `client_email`. Use “Make changes to events” permission for writing; read-only shares support reading and announcements.

Connect the JSON file and give the account a name, then **Fetch calendars**. Sharing does not automatically put every calendar in the service account's calendar list. For missing calendars, copy the calendar ID from Google Calendar settings → Integrate calendar and use **Add shared calendar**. Select subscriptions and save. Multiple accounts are supported.

The long-lived credential is the service account private key, not a permanent access token. Assister signs a service account assertion and exchanges it at Google's fixed token endpoint; it caches short-lived access tokens and renews before expiration. Credential-provided token endpoints are never used. Domain-wide delegation and user OAuth sign-in are not implemented.

## Events

Choose a subscription and date range, then fetch events. Results are paginated. Create timed events in the browser's local zone or all-day events with an exclusive end date. Edit preserves fields outside the editor, including existing notification settings unless explicitly changed. Google etags prevent overwriting a concurrent edit. Editing/deleting an expanded recurring occurrence affects that instance; this page does not edit whole recurrence rules or guests.

Removal first fetches the event and prepares a five-minute single-use confirmation, then displays a confirmation dialog. Only **Confirm deletion** performs the Google DELETE with the fetched etag. There is no direct event DELETE endpoint. Assistant tools similarly prepare first and require a later explicit “confirm delete” message, scoped to the satellite and conversation. Restarting invalidates pending confirmations.

Assistant tools: `calendar_list`, `calendar_events`, `calendar_create`, `calendar_edit`, `calendar_delete`. Use requests such as “What's on my Google calendar tomorrow?”, “Add a dentist appointment to my family calendar tomorrow from 2 PM to 3 PM”, and “Move that event to 4 PM”. Clarify dates, calendars and titles when ambiguous. Tools use subscribed calendars only; create/edit require a current explicit mutation request. The dashboard also supports descriptions, locations, all-day dates, and disabling notifications.

## Announcements

For each subscription choose a satellite, enable **Announce configured notifications**, and save. Notifications are polled every 30 seconds, using expanded recurring events. Both popup and email notification lead times become spoken announcements; duplicate lead times are collapsed. Explicit empty overrides disable announcements for that event. Events using calendar defaults inherit the service account's default reminders, configurable on this page.

Google reminder preferences are per user: a calendar owner's personal notification settings are not necessarily the service account's settings. Configure the service account defaults or event notification overrides through Assister. Sharing a calendar alone does not copy another user's notification preferences.

The poller covers Google's maximum 28-day reminder lead time and a five-minute grace period for delayed notifications. It retries busy/offline satellites during that grace period and skips events that have already started, except at-start notifications which retain the five-minute grace period. Successful delivery keys persist in SQLite per account, calendar, event occurrence, notification lead time and satellite. Delivery is at least once: a crash between playback and recording completion can repeat an announcement. Announcements use the existing satellite TTS/playback path. `/api/calendars/status` reports the most recent polling pass and sanitized failure; these are not physical playback verification.

## Storage and access

`google-calendars.json` under `Assister:DataPath` stores account metadata, subscription settings and encrypted credentials. Data Protection keys persist under `calendar-protection-keys` in the same directory. Preserve and protect both in backups; directory access grants the ability to decrypt credentials. Keys and credentials are never returned by account APIs. These management endpoints follow the existing trusted-network dashboard access model. This integration does not add internet-facing dashboard authentication.

No credentials or live Google account were supplied during implementation. Local tests use generated RSA keys, fake Google responses and isolated SQLite/data directories. Live Google permissions, actual reminder preferences, and physical announcement playback require validation with a configured account and satellite.

References: [Google service account OAuth](https://developers.google.com/identity/protocols/oauth2/service-account), [Calendar events](https://developers.google.com/workspace/calendar/api/v3/reference/events), [Adding calendars to a calendar list](https://developers.google.com/workspace/calendar/api/v3/reference/calendarList/insert).
