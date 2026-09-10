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
