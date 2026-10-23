# Request Logger

The `request-logger` plugin owns its configuration, localization, capture middleware,
asynchronous writer and log viewer. No database or additional service is required.

## Configuration

Place these options in the existing `request-logger` plugin configuration block:

```yaml
Enabled: true
LogToFile: true
LogToEvent: false
LogDirectory: logs/request_logger
Format: json
MaxBodySize: 65536
QueueCapacity: 256
MaxQueuedBytes: 16777216
BatchSize: 64
FlushIntervalMs: 1000
MaxFileBytes: 67108864
MaxFiles: 30
RetentionDays: 14
SensitiveFields:
  - payment_secret
MatchRules:
  - Host: '*'
    Path: /api/
    IgnorePaths: /api/health
    Methods: GET,POST,PUT,DELETE
    StatusCodes: [4xx, 5xx]
    MinDurationMs: 0
    SampleRate: 1
    LogHeaders: false
    LogQueryString: true
    LogRequestBody: false
    LogResponseBody: false
    LogDuration: true
    FilePrefix: api-errors
```

`text` is the default and preserves the existing text log viewer format. `json`
writes JSON Lines (`.jsonl`), supported by the plugin's existing **View logs** action.
The legacy host text-log query endpoint remains text-only.

Rules retain first-match behavior: conditions inside a rule are AND; rules are
considered in order. Methods, status codes, duration and sampling are additional
filters. Empty status/method filters accept all; `20x`, `30x`, `4xx`, exact codes
and `*` are supported. Sampling 0 disables normal records, 1 records every match.
Interception records are independent and are not suppressed by normal sampling.
Without rules, all paths except `/health` and `/favicon.ico` prefixes are logged.

## Safety And Operation

- Authorization, cookies, passwords, tokens, API keys and signatures are redacted
  by default. `SensitiveFields` adds field names, not replacements for defaults.
  Matching ignores case and non-alphanumeric separators.
- Body capture is opt-in and bounded. Only complete JSON and URL-encoded forms
  are rendered with field redaction. Invalid, truncated, binary and other body
  formats are omitted. Response bytes continue to the client immediately; the
  logger does not buffer the entire response or read an unread request body.
- Field-based redaction cannot identify arbitrary secrets in path segments,
  ordinary free-text values, or interception reasons. Avoid placing secrets there.
  Configure additional sensitive names for application-specific fields.
- JSON records include a request ID, timestamp, status, duration and client IP.
  Request/response byte fields are declared Content-Length, not measured wire
  traffic. Client cancellation is recorded as 499; an escaped downstream
  exception with an otherwise successful status is recorded as 500.
- File writes use a bounded, non-blocking queue. Full batches flush immediately;
  partial batches wait at most the flush interval. Explicit flush and graceful
  stop do not wait for that interval. The byte budget includes records already
  dequeued and waiting for disk, not just records still inside the channel.
  Overflow drops log records, not requests. **View logs** exposes written,
  pending, dropped and failed-record counts, pending bytes, rotation/cleanup
  failure counts and the last error type/time. Refresh the file list to update
  them. The `status` action reports metrics without waiting for disk or flushing.
  Counters reset when the plugin starts. Failed records include records that
  could not be written because rotation failed; successfully flushed records
  remain counted as written even if a later record or cleanup fails.
- Graceful stop drains the queue within the host shutdown deadline. This is
  best-effort access logging, not durable audit storage: a crash can lose queued
  records. `LogToEvent` uses the existing NLog configuration independently.
- Queue capacity/byte limit changes require restarting the plugin. Other write
  policy changes apply to subsequently enqueued records.
- Rotation happens before the next record would exceed the size limit. A single
  record is never split and can exceed that limit. Retention is checked on the
  first successful write, after rotation or retention-policy changes, and at
  most once per minute during ordinary writes. It covers this plugin's owned
  files, retaining the active file. Cleanup errors are reported independently
  and do not invalidate successful writes. Unrelated files and links are not
  deleted. No cleanup timer runs when logging is idle.
- New custom prefixes are sanitized and begin with `req_`; intercept files use
  `intercept_`. Existing custom-prefix logs remain visible while their matching
  configuration is retained. The viewer serializes mutations with the writer.
  Read-only queries acquire that lock only to open a pinned file handle and
  capture its length. Parsing then runs outside the write lock; appends,
  rotations and atomic clear/rewrite operations do not change the captured
  prefix. JSON pagination scans that prefix backwards once, retaining only the
  requested page, with an 8 MiB record/page bound and cancellation support.
  The existing offset/total API is preserved, so exact totals still require
  scanning the snapshot; this is not an indexed or cursor-based database.
  Log paths containing filesystem links are rejected; Unix files are created
  owner-readable/writable and directories owner-accessible only.

## References

The design draws on [NGINX access logging](https://nginx.org/en/docs/http/ngx_http_log_module.html)
for buffered/conditional logging and [Caddy logging](https://caddyserver.com/docs/caddyfile/directives/log)
for structured output, rotation, retention and default credential redaction.
