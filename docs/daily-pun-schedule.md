# Daily pun schedule configuration

BeanBot posts the automatic daily pun according to a configured local wall-clock time and timezone. Existing deployments do not need to add new settings: the default remains **16:20 America/Chicago**.

## Settings

```env
BEANBOT_DAILY_PUN_TIME=16:20
BEANBOT_DAILY_PUN_TIMEZONE=America/Chicago
```

`BEANBOT_DAILY_PUN_TIME` must use 24-hour `HH:mm` format. `BEANBOT_DAILY_PUN_TIMEZONE` accepts a timezone ID recognized by the .NET runtime. IANA IDs such as `America/Chicago` and Windows IDs such as `Central Standard Time` are resolved through the runtime's cross-platform timezone conversion support.

The schedule is validated when BeanBot starts. A malformed time or unknown timezone stops startup with a diagnostic that names the affected setting without echoing its configured value. Schedule settings are immutable for the process lifetime, so changing either environment variable requires a normal BeanBot restart.

## Daylight-saving behavior

Scheduling uses the configured timezone rather than the host machine's local timezone.

- If the configured wall-clock time does not exist on a spring-forward date, BeanBot moves that occurrence forward by one hour, matching the legacy scheduler behavior.
- If the configured wall-clock time occurs twice on a fall-back date, BeanBot selects one standard-time occurrence rather than scheduling both.

The automatic schedule changes only when these settings change. The `%pun` command and the existing three-message daily-pun output are unchanged.

## Restart recovery and duplicate suppression

The automatic scheduler can catch up for **45 minutes** after an occurrence, including when that window crosses local midnight. It uses the scheduled occurrence's date in the configured timezone. After the grace window closes, it advances to the next occurrence.

Before starting the three-message sequence, BeanBot atomically claims the date in MongoDB's `dailyPunCheckpoint` collection. The single checkpoint only advances to later dates. Restarting or overlapping instances cannot claim the same or an earlier date again. Keep this collection when backing up or restoring bot data.

Missing channels, unavailable puns, and claim-store failures retry before sending, at most once every 30 seconds and only inside the grace window. Claim waits are bounded, and a stalled claim retains its operation slot until it finishes. MongoDB must acknowledge a claim before any message is sent.

The grace deadline controls when a sequence may start; an admitted sequence may finish after it. Once claimed, a failed or ambiguous send is never retried automatically. This favors avoiding duplicate posts: a crash after claiming can leave that day's sequence incomplete. Changing the timezone to an earlier local date can also suppress that date until the schedule advances beyond the stored checkpoint.
