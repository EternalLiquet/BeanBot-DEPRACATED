# Persistent logging operations

BeanBot writes its persistent Serilog file logs beneath `BeanBotFiles/Logs`. In the documented container deployment this directory is part of the writable `/app/BeanBotFiles` volume; console logs remain separate and are not subject to the file-retention policy below.

## Storage and rollover policy

Persistent file logging uses both daily rolling and size-triggered rolling. Each file has a configured 25 MiB size limit and BeanBot retains at most eight matching log files. The configured retention envelope is therefore approximately 200 MiB. Serilog rolls to the next sequence when the current file reaches the size threshold and removes older rolled files as newer files are opened. Individual rendered events can cross the threshold at an event boundary, so operators should treat 200 MiB as the intended retention envelope rather than a filesystem quota.

These limits are repository-owned constants in `FileLogPolicy`; changing them should be intentional and accompanied by verification and documentation updates.

## Async buffering and log loss

File I/O stays behind `Serilog.Sinks.Async` so a slow disk does not synchronously hold Discord or application threads. BeanBot configures an explicit 2,048-event in-memory buffer with `blockWhenFull: false`. If the worker cannot keep up and the buffer fills, new file-log events are dropped until capacity becomes available instead of creating an unbounded queue or blocking application work.

`FileLogDropMonitor` samples the sink's cumulative dropped-event counter every 30 seconds and also performs a final sample when the async sink stops. When new drops are observed it writes one coalesced summary directly to `Console.Error`. The diagnostic includes counts only; it does not copy dropped event payloads and does not re-enter Serilog or the Discord owner-alert sink.

## Shutdown behavior

Normal shutdown still asks Serilog to close and flush its sinks. BeanBot gives that final flush a five-second application-owned budget. If file logging is blocked long enough to exceed the budget, shutdown continues and a short diagnostic is written directly to `Console.Error`; any eventual late flush fault is observed so it cannot become an unobserved task failure. Immediate file-I/O or disposed-writer failures are likewise reported out of band without including the original log payload.

The overall Generic Host shutdown budget remains two minutes, and the documented container stop grace period remains 130 seconds. The five-second file-log flush budget is inside that larger lifecycle budget and is specifically intended to keep a broken or full filesystem from pinning process exit.

## Operational checks

After changing logging policy or container filesystem behavior, run `./scripts/verify.sh full`. The full gate builds the production image and runs the non-root/read-only container smoke test, which protects compatibility with the writable `BeanBotFiles` volume.
