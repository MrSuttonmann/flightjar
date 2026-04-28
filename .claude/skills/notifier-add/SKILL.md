---
name: notifier-add
description: Add a new notification channel type (Discord, Slack, Pushover, Matrix, SMTP, Signal, …) to the alerts pipeline. Covers the new sealed-record subclass, the JsonConverter branch, the INotifier impl, DI registration in Program.cs, the Alerts dialog UI (FIELDS map + add button + persistence), and the notifier tests. Use when the user asks to add a new notification channel ("send alerts to Discord", "support Pushover", "wire up Matrix alerts"). Don't use for changing existing notifier behaviour (Telegram formatting, ntfy priority map, etc) — those are one-file edits to the specific notifier.
---

# Adding a new notification channel

Notification channels are user-managed: three types ship today
(Telegram / ntfy / webhook), each stored in
`/data/notifications.json` and edited through the Alerts dialog.
The model is polymorphic — `NotificationChannel` is an abstract
record with a sealed subclass per kind — so adding a fourth type
is mostly "create a new subclass + wire two switches". The wire
shape stays flat per v1, discriminated by a snake_case `type`
string, and the custom `NotificationChannelJsonConverter` is the
only place that knows how to map the discriminator to the right
subclass.

If you just want to tweak an existing notifier's upstream API
shape — different Telegram formatting, ntfy priority map — edit
the specific `<Kind>Notifier.cs` directly. This skill is for a
brand new channel kind.

## Touchpoints

1. **`dotnet/src/FlightJar.Persistence/Notifications/NotificationChannel.cs`**
   — new value in `NotificationChannelType`, plus a new
   `<Kind>Channel` sealed record subclass with the type-specific
   fields and an `IsReady()` override.
2. **`dotnet/src/FlightJar.Persistence/Notifications/NotificationChannelJsonConverter.cs`**
   — one branch each in `Read` (deserialise into the new subclass)
   and `Write` (emit the per-type fields in legacy v1 order).
3. **`dotnet/src/FlightJar.Persistence/Notifications/NotificationsConfigStore.cs`**
   — one branch in `Clean()` to trim the new subclass's fields.
4. **`dotnet/src/FlightJar.Notifications/<Kind>Notifier.cs`**
   (new file) — `INotifier` impl that posts to the upstream.
5. **`dotnet/src/FlightJar.Api/Program.cs`** — two lines:
   `AddHttpClient<KindNotifier>()` and
   `AddSingleton<INotifier, KindNotifier>()`.
6. **`app/static/alerts_dialog.js`** — new `TYPE_LABELS` entry,
   new `FIELDS[<kind>]` array, and possibly a secret-field opt-in.
7. **`app/static/index.html`** — new `<button class="alerts-add" data-type="<kind>">` in the add-channel row.
8. **`dotnet/tests/FlightJar.Notifications.Tests/`** — a
   `<Kind>NotifierTests.cs` driven by a mock HTTP handler, and an
   entry in `DispatcherTests` if the kind has distinctive fan-out
   behaviour.
9. **`CLAUDE.md` / `README.md`** — one-line mention in the
   Watchlist + alerts section; the README's feature list.

## Step 1 — Enum + subclass

Open `NotificationChannel.cs`. Add the enum value in sort order
with the others, then declare a `sealed record <Kind>Channel :
NotificationChannel` carrying the type-specific fields. Field
defaults stay empty-string so legacy rows deserialise cleanly.
Override `Type` to return the new enum value and `IsReady()` to
require whatever the upstream needs.

```csharp
public enum NotificationChannelType
{
    Telegram,
    Ntfy,
    Webhook,
    Discord,          // ← new
}

public sealed record DiscordChannel : NotificationChannel
{
    public override NotificationChannelType Type => NotificationChannelType.Discord;
    public string Url { get; init; } = "";

    public override bool IsReady() => !string.IsNullOrEmpty(Url);
}
```

Reuse the field name `Url` when the upstream's auth/target shape
matches an existing kind — Discord's `https://discord.com/api/webhooks/…`
is a bare POST target, so `Url` is the right name. Only invent a
new field when the semantics differ — Telegram's
`BotToken`/`ChatId` pair is type-distinctive enough to live
separately on `TelegramChannel`.

## Step 2 — JsonConverter

`NotificationChannelJsonConverter.Read` switches on the `type`
string and instantiates the right subclass; `Write` writes the
common fields first then the per-type ones. Add both branches:

```csharp
// Read:
var kind = typeStr.ToLowerInvariant() switch
{
    // ...existing branches...
    "discord" => NotificationChannelType.Discord,
    _ => null,
};

// in the kind switch:
NotificationChannelType.Discord => new DiscordChannel
{
    Id = id ?? "",
    Name = name ?? "",
    Enabled = enabled,
    WatchlistEnabled = watchlistEnabled,
    EmergencyEnabled = emergencyEnabled,
    Url = ReadString(root, "url") ?? "",
},
```

```csharp
// Write switch:
case DiscordChannel d:
    writer.WriteString("url", d.Url);
    break;
```

```csharp
// TypeToString:
NotificationChannelType.Discord => "discord",
```

If you forget the converter branch, the channel will fail
deserialisation entirely and the log will show "no matching
discriminator" — much louder than the legacy fall-through-to-null
behaviour, but still worth wiring before testing.

## Step 3 — Config-store Clean()

`NotificationsConfigStore.Clean(raw)` normalises a fresh record
(default name, generated id, trimmed fields). Add a branch to the
type switch:

```csharp
return common switch
{
    // ...existing branches...
    DiscordChannel d => d with { Url = (d.Url ?? "").Trim() },
    _ => null,
};
```

If you miss this branch, `Clean` will fall through the `_ =>
null` catch-all and the channel will **silently disappear** on
the very next save (the UI saves on every edit, so it'll vanish
before the user finishes typing). This is the most common "why
is my channel gone?" bug.

## Step 4 — The notifier

Create `dotnet/src/FlightJar.Notifications/<Kind>Notifier.cs`
implementing `INotifier`. Shortest template, modelled on
`WebhookNotifier`:

```csharp
using System.Net.Http.Json;
using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace FlightJar.Notifications;

public sealed class DiscordNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<DiscordNotifier> _logger;

    public NotificationChannelType Kind => NotificationChannelType.Discord;

    public DiscordNotifier(HttpClient http, ILogger<DiscordNotifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task SendAsync(NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        if (channel is not DiscordChannel d || !d.IsReady())
        {
            return Task.CompletedTask;
        }
        return SendAsync(msg, d, ct);
    }

    private async Task SendAsync(NotificationMessage msg, DiscordChannel channel, CancellationToken ct)
    {
        var payload = new { content = $"**{msg.Title}**\n{msg.Body}" };
        try
        {
            using var resp = await _http.PostAsJsonAsync(channel.Url, payload, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "discord send failed");
        }
    }
}
```

Load-bearing conventions:

- **Pattern-match the channel to the subclass at entry** with
  `is not <Kind>Channel`. The dispatcher already filters by
  `Kind`, but a stray direct call or a misrouted channel
  shouldn't POST against a half-configured endpoint.
- **Catch `Exception` but exclude `OperationCanceledException`.**
  OCE propagates intentionally — the dispatcher is fire-and-forget
  and cancellation is a clean shutdown signal, not a failure to log.
- **Log warnings, don't throw.** A slow or dead upstream for one
  channel must not block the others — `NotifierDispatcher` fans
  out via `Task.WhenAll`.
- **Escape upstream-specific reserved characters** if the upstream
  uses a markup dialect. Put the escape helper on the notifier,
  not in a shared place — each upstream has incompatible rules.

## Step 5 — Register in Program.cs

Two lines near the other notifiers:

```csharp
builder.Services.AddHttpClient<DiscordNotifier>();
builder.Services.AddSingleton<INotifier, DiscordNotifier>();
```

`NotifierDispatcher` picks up every `INotifier` from DI and
indexes them by `Kind`, so there's nothing to edit in the
dispatcher itself.

## Step 6 — Alerts dialog

In `app/static/alerts_dialog.js`:

```js
const TYPE_LABELS = {
  telegram: 'Telegram chats',
  ntfy: 'ntfy topics',
  webhook: 'Webhooks',
  discord: 'Discord webhooks',         // ← new
};

const FIELDS = {
  // ...existing entries...
  discord: [
    { key: 'url', label: 'Webhook URL', placeholder: 'https://discord.com/api/webhooks/…' },
  ],
};
```

Then grouping + rendering + persistence work automatically for
the new kind — the render loop iterates every type in
`TYPE_LABELS` order and the save path sends the whole
`state.channels` array to `/api/notifications/config`. The
frontend sends snake_case keys (`bot_token`, `chat_id`, `url`,
…), which matches the `NotificationChannelJsonConverter`'s flat
v1 wire format exactly.

Add a button in `app/static/index.html` to let users create a
channel of the new type:

```html
<button type="button" class="alerts-add" data-type="discord">+ Discord webhook</button>
```

If the new kind has a secret field (API token, bot token), flag
it with `secret: true` in the FIELDS entry — the dialog will
render a password input with a show/hide eye toggle automatically.

## Step 7 — Tests

Add `<Kind>NotifierTests.cs` under
`dotnet/tests/FlightJar.Notifications.Tests/` using the same
`HttpMessageHandler` mock pattern as the existing suites. Cover:

- Happy path — `IsReady()` channel → one HTTP POST with expected
  payload shape.
- `IsReady() == false` (empty URL / token / whatever) — no HTTP
  traffic.
- Wrong channel subtype handed in (e.g. a `WebhookChannel` to
  `DiscordNotifier.SendAsync`) — no HTTP traffic (defence in depth).
- Upstream 4xx / 5xx — logged warning, no throw.
- Cancellation — `OperationCanceledException` propagates.

If the kind changes dispatcher-level behaviour (e.g. it targets a
broadcast room rather than a user), add a test to `DispatcherTests`
covering the new `ChannelAcceptsCategory` case or whatever's
distinctive.

## Step 8 — Docs

- **`CLAUDE.md`** — extend the "Three notifier types" enumeration
  in `### Watchlist + alerts` with the new type. One sentence
  about what gets sent and any auth model.
- **`README.md`** — add the new type to the Alerts feature
  description in the user-facing section.

## Step 9 — Verify

```bash
cd dotnet
dotnet format FlightJar.slnx --verify-no-changes
dotnet test tests/FlightJar.Notifications.Tests/
dotnet test tests/FlightJar.Persistence.Tests/   # exercises Clean() + the converter
dotnet test FlightJar.slnx
cd ..
node --test tests/js/
```

Manual check: run the backend, open the Alerts dialog, click the
new `+` button, fill in a placeholder URL, click Test. Check the
server log — a warning log line indicates the request went out
and the upstream rejected it; silence indicates `IsReady()` is
returning false (misconfigured field) or `Clean()` stripped the
URL on save.

## Diagnostics

If the channel "disappears" when the user edits it, the culprit
is almost always one of:

- `Clean()` missing a branch for the new subclass → returns null,
  channel dropped on save.
- `NotificationChannelJsonConverter.Read` missing a branch for
  the new discriminator → deserialises to null, channel dropped
  on load.
- The new subclass declared on the wrong base, or `Type` not
  overridden → `Clean()`'s pattern match misses it.

If the channel saves fine but nothing sends, check: is the
`INotifier` registered in `Program.cs`? `NotifierDispatcher.DispatchAsync`
silently skips a channel when no matching `INotifier` is in its
dictionary. A startup info log line lists the configured channels
(`ConfiguredSummary()`) — compare its output against the expected
kinds.

## Do not

- Do not skip the `Clean()` branch or the converter `Read`/`Write`
  branches. Each one independently can delete user data on the
  next save / load.
- Do not leak secrets into log messages. The existing notifiers
  log `{Kind}` and the channel `{Id}` but never the bot token /
  webhook URL. Follow suit — warning logs can end up in bug reports.
- Do not introduce a shared Markdown escape helper that tries to
  cover multiple upstreams. Each chat platform has its own
  reserved-char set and "almost Markdown" is a treacherous
  abstraction.
- Do not authenticate via query-string tokens in the upstream
  URL. If the API wants a bearer, add a `Token` field on the
  subclass (or reuse the existing one if it's just a secret
  string), and set it via `Authorization: Bearer …`. Tokens in
  URLs get logged by proxies, stored in browser history, and
  echoed into error messages.
- Do not expand `NotificationMessage` shape just to fit a
  specific upstream's schema. Every notifier maps from the same
  `{Title, Body, Level, Url, PhotoUrl}` into its own payload —
  if an upstream needs a field the message doesn't carry (e.g.
  Slack attachment blocks), derive it inside the notifier from
  the fields that exist.
- Do not ignore the `AlertCategory` `WatchlistEnabled` /
  `EmergencyEnabled` per-channel flags. Those checks live in
  `NotifierDispatcher.ChannelAcceptsCategory` — don't filter
  there in the notifier itself.
- Do not bypass the `NotificationChannelJsonConverter` by adding
  `JsonPropertyName` attributes to your subclass. The converter
  owns the wire shape end-to-end; competing attributes will
  silently shadow it.
