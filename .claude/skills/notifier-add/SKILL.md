---
name: notifier-add
description: Add a new notification channel type (Discord, Slack, Pushover, Matrix, SMTP, Signal, …) to the alerts pipeline. Covers INotifier impl, extending NotificationChannel + NotificationChannelType + IsReady + the config-store Clean() switch, DI registration in Program.cs, the Alerts dialog UI (FIELDS map + add button + persistence), and the notifier tests. Use when the user asks to add a new notification channel ("send alerts to Discord", "support Pushover", "wire up Matrix alerts"). Don't use for changing existing notifier behaviour (Telegram formatting, ntfy priority map, etc) — those are one-file edits to the specific notifier.
---

# Adding a new notification channel

Notification channels are user-managed: three types ship today
(Telegram / ntfy / webhook), each stored in
`/data/notifications.json` and edited through the Alerts dialog.
Adding a fourth type is a coherent multi-file change — there's
no single "channel registry", so the type enum, the record
fields, the notifier implementation, the config-store cleaner,
the dispatcher wiring, the dialog form, and the tests all get a
new line each. Get any one of them wrong and the channel
silently disappears on load (see **Diagnostics** at the bottom).

If you just want to tweak an existing notifier's upstream
API shape — different Telegram formatting, ntfy priority map —
edit the specific `<Kind>Notifier.cs` directly. This skill is
for a brand new channel kind.

## Touchpoints

1. **`dotnet/src/FlightJar.Persistence/Notifications/NotificationChannel.cs`**
   — new enum value in `NotificationChannelType`, any new
   type-specific fields on the `NotificationChannel` record,
   and a new branch in `IsReady()`.
2. **`dotnet/src/FlightJar.Persistence/Notifications/NotificationsConfigStore.cs`**
   — new branch in `Clean(raw)` that strips leaked cross-type
   values and preserves only the fields your kind uses.
3. **`dotnet/src/FlightJar.Notifications/<Kind>Notifier.cs`**
   (new file) — `INotifier` impl that posts to the upstream.
4. **`dotnet/src/FlightJar.Api/Program.cs`** — two lines:
   `AddHttpClient<KindNotifier>()` and
   `AddSingleton<INotifier, KindNotifier>()`.
5. **`app/static/alerts_dialog.js`** — new `TYPE_LABELS` entry,
   new `FIELDS[<kind>]` array, and possibly a secret-field
   opt-in.
6. **`app/static/index.html`** — new `<button class="alerts-add" data-type="<kind>">` in the add-channel row.
7. **`dotnet/tests/FlightJar.Notifications.Tests/`** — a
   `<Kind>NotifierTests.cs` driven by a mock HTTP handler, and
   an entry in `DispatcherTests` if the kind has distinctive
   fan-out behaviour.
8. **`CLAUDE.md` / `README.md`** — one-line mention in the
   Watchlist + alerts section; the README's feature list.

## Step 1 — Enum + fields + IsReady

Open `NotificationChannel.cs`. Add the enum value in sort
order with the others, and declare any type-specific fields on
the record. Record fields stay nullable-empty-string defaults so
legacy rows deserialise cleanly. Wire a branch into `IsReady()`
so the dispatcher skips the channel until the user has filled
everything in.

```csharp
public enum NotificationChannelType
{
    Telegram,
    Ntfy,
    Webhook,
    Discord,          // ← new
}

public sealed record NotificationChannel
{
    // ...existing fields...

    // Discord-specific
    public string DiscordWebhookUrl { get; init; } = "";

    public bool IsReady() => Type switch
    {
        // ...existing branches...
        NotificationChannelType.Discord => !string.IsNullOrEmpty(DiscordWebhookUrl),
        _ => false,
    };
}
```

Reuse existing fields when semantically identical — a bare POST
target is called `Url`, so a Discord webhook URL could live in
`Url` rather than a dedicated `DiscordWebhookUrl`. Follow the
existing convention (ntfy + webhook both reuse `Url`). Only
invent a new field when the semantics differ — Telegram's
`BotToken` / `ChatId` pair is type-distinctive enough to live
separately.

## Step 2 — Config-store Clean()

`NotificationsConfigStore.Clean(raw)` is what turns a raw
user-supplied record into the persisted shape. It does two
jobs: dropping unknown enum values, and stripping fields that
don't belong to the channel's type (so a Telegram entry can't
smuggle a `Url` from a reused form). Add your branch:

```csharp
return raw.Type switch
{
    // ...existing branches...
    NotificationChannelType.Discord => cleaned with
    {
        Url = (raw.Url ?? "").Trim(),  // or DiscordWebhookUrl if you chose that shape
    },
    _ => null,
};
```

If you miss this branch, `Clean` will fall through the `_ =>
null` catch-all and the channel will **silently disappear** on
the very next save (the UI saves on every edit, so it'll vanish
before the user finishes typing). This is the most common
"why is my channel gone?" bug.

## Step 3 — The notifier

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

    public async Task SendAsync(
        NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        if (channel.Type != NotificationChannelType.Discord || !channel.IsReady())
        {
            return;
        }
        var payload = new
        {
            content = $"**{msg.Title}**\n{msg.Body}",
            // …any upstream-specific shape…
        };
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

- **Guard the Type + IsReady check at entry.** The dispatcher
  already checks both, but a stray direct call or a misrouted
  channel shouldn't POST against a half-configured endpoint.
- **Catch `Exception` but exclude `OperationCanceledException`.**
  OCE propagates intentionally — the dispatcher is fire-and-forget
  and cancellation is a clean shutdown signal, not a failure to
  log.
- **Log warnings, don't throw.** A slow or dead upstream for one
  channel must not block the others — `NotifierDispatcher` fans
  out via `Task.WhenAll` and a throwing notifier would surface as
  an observed exception even though the dispatcher wraps each call
  in `SafeSendAsync`.
- **Escape upstream-specific reserved characters** if the
  upstream uses a markup dialect (Telegram's MarkdownV2 needs
  `\` before `_*[](){}…!`; Discord's `**` bold is tolerant;
  Slack's mrkdwn has its own). Put the escape helper on the
  notifier, not in a shared place — each upstream has
  incompatible rules.

## Step 4 — Register in Program.cs

Two lines near the other notifiers (currently around line 180):

```csharp
builder.Services.AddHttpClient<DiscordNotifier>();
builder.Services.AddSingleton<INotifier, DiscordNotifier>();
```

`NotifierDispatcher` picks up every `INotifier` from DI and
indexes them by `Kind`, so there's nothing to edit in the
dispatcher itself.

## Step 5 — Alerts dialog

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
…), which matches the server's `ConfigureHttpJsonOptions` policy
exactly.

Add a button in `app/static/index.html` to let users create a
channel of the new type:

```html
<button type="button" class="alerts-add" data-type="discord">+ Discord webhook</button>
```

If the new kind has a secret field (API token, bot token), flag
it with `secret: true` in the FIELDS entry — the dialog will
render a password input with a show/hide eye toggle
automatically.

## Step 6 — Tests

Add `<Kind>NotifierTests.cs` under
`dotnet/tests/FlightJar.Notifications.Tests/` using the same
`HttpMessageHandler` mock pattern as the existing suites. Cover:

- Happy path — `IsReady()` channel → one HTTP POST with expected
  payload shape.
- `IsReady() == false` (empty URL / token / whatever) — no HTTP
  traffic.
- Wrong `channel.Type` — no HTTP traffic (defence in depth).
- Upstream 4xx / 5xx — logged warning, no throw.
- Cancellation — `OperationCanceledException` propagates.

If the kind changes dispatcher-level behaviour (e.g. it targets
a broadcast room rather than a user), add a test to
`DispatcherTests` covering the new `ChannelAcceptsCategory` case
or whatever's distinctive.

## Step 7 — Docs

- **`CLAUDE.md`** — extend the "Three notifier types" enumeration
  in `### Watchlist + alerts` with the new type. One sentence
  about what gets sent and any auth model.
- **`README.md`** — add the new type to the Alerts feature
  description in the user-facing section.

## Step 8 — Verify

```bash
cd dotnet
dotnet format FlightJar.slnx --verify-no-changes
dotnet test tests/FlightJar.Notifications.Tests/
dotnet test tests/FlightJar.Persistence.Tests/   # exercises Clean()
dotnet test FlightJar.slnx
cd ..
node --test tests/js/
```

Manual check: run the backend, open the Alerts dialog, click the
new `+` button, fill in a placeholder URL, click Test. Check the
server log — a warning log line indicates the request went
out and the upstream rejected it; silence indicates `IsReady()`
is returning false (misconfigured field) or `Clean()` stripped
the URL on save.

## Diagnostics

If the channel "disappears" when the user edits it, the culprit
is almost always `Clean()` — either you added the enum value
without a matching `Clean()` branch (→ returns `null`, channel
dropped), or the branch is there but doesn't preserve your new
fields (→ branch returns a cleaned row but the fields are empty
so `IsReady()` returns false on the next load).

If the channel saves fine but nothing sends, check: is the
`INotifier` registered in `Program.cs`?
`NotifierDispatcher.DispatchAsync` silently skips a channel when
no matching `INotifier` is in its dictionary. A startup info log
line lists the configured channels (`ConfiguredSummary()`) —
compare its output against the expected kinds.

## Do not

- Do not skip the `Clean()` branch. It's the one edit every
  newcomer forgets and it deletes user data.
- Do not leak secrets into log messages. The existing notifiers
  log `{Kind}` and the channel `{Id}` but never the bot token /
  webhook URL. Follow suit — warning logs can end up in
  bug reports.
- Do not introduce a shared Markdown escape helper that tries
  to cover multiple upstreams. Each chat platform has its own
  reserved-char set and "almost Markdown" is a treacherous
  abstraction.
- Do not authenticate via query-string tokens in the upstream
  URL. If the API wants a bearer, add a `Token` field (or reuse
  the existing one if it's just a secret string), and set it via
  `Authorization: Bearer …`. Tokens in URLs get logged by
  proxies, stored in browser history, and echoed into error
  messages.
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
