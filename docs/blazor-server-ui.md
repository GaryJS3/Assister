# Blazor Server management UI

The management interface is implemented by Razor components under
`src/Assister/Components`, using **.NET 10 interactive server rendering**.
UI markup, page state, validation, navigation, and actions execute as C# on the
ASP.NET Core server. The page endpoints support both clean URLs and the
original `.html` bookmarks; all old static HTML entry points were removed.

- `Components/Shared/MainLayout.razor`: common responsive sidebar.
- `Components/Shared/PageHeader.razor`, `Notice.razor`, `JsonDetails.razor`:
  reusable UI elements.
- `Components/Services/BrowserApi.cs`: common browser-to-existing-API
  transport used by interactive components.
- `wwwroot/ui.css`: unified visual system.
- `wwwroot/ui-interop.js`: browser-only adapters for fetch, storage,
  clipboard, confirmation, local dates, file uploads and microphone capture.

The API transport intentionally sends requests **from the browser**, rather
than from the server circuit via a loopback HTTP client, to preserve existing
session-cookie behavior and avoid accidentally bypassing client ownership
authorization. Administration APIs retain their original security model.
The management UI is **not** an authentication boundary: place it behind
trusted network access or an authenticated reverse proxy.

The original page-specific JavaScript and CSS files remain in the repository
as non-loaded legacy references and for existing browser regression tests.
They are not linked or executed by the Blazor UI. They can be removed after
the browser test suite has been migrated.

## Operations and validation

Use `dotnet run --project src/Assister` or the existing Docker build.
No separate frontend bundler is needed. Build with
`dotnet build Assister.sln --configuration Release`.
The server needs working WebSocket connectivity for Blazor's SignalR circuit
as well as the pre-existing audio and rich-client protocols.

The conversation page polls durable interaction events, rather than opening
a second WebSocket for every interaction. Rich-client audio responses use
the existing ordered segment player with automatic playback and completion acknowledgements. Retained recordings remain available through standard browser audio controls. File package uploads are sent
directly from the browser to the existing API, avoiding large files over
Blazor's circuit. Reminders, calendars, intents and device management
continue using the established API contracts.

The frontend API permits actions that change device state; exercise care on
the diagnostic chat, intent execution, satellite test, and administrator
credential pages.

API JSON responses are transferred with bounded JS interop streams (16 MiB maximum),
so diagnostic histories larger than SignalR's 32 KiB per-message limit do not disconnect the circuit.
The conversation page renews its capability registration every 30 seconds.

