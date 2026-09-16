# command regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# cmd-01--query | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%3B%20whoami
X-Hurl-Case: cmd-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--query-after
HTTP 200


# cmd-01--query-double | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253B%2520whoami
X-Hurl-Case: cmd-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--query-double-after
HTTP 200


# cmd-01--query-duplicate | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3B%20whoami
X-Hurl-Case: cmd-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--query-duplicate-after
HTTP 200


# cmd-01--header | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: cmd-01--header
X-Probe: %3B%20whoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--header-after
HTTP 200


# cmd-01--cookie | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: cmd-01--cookie
Cookie: probe=%3B%20whoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--cookie-after
HTTP 200


# cmd-01--form | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%3B%20whoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--form-after
HTTP 200


# cmd-01--json | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--json
Content-Type: application/json
```
{"data":{"values":["normal","; whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--json-after
HTTP 200


# cmd-01--json-put | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: cmd-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","; whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--json-put-after
HTTP 200


# cmd-01--json-patch | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: cmd-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","; whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--json-patch-after
HTTP 200


# cmd-01--text | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--text
Content-Type: text/plain
```
; whoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--text-after
HTTP 200


# cmd-01--xml | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--xml
Content-Type: application/xml
```
<root><value>; whoami</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--xml-after
HTTP 200


# cmd-01--multipart | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KOyB3aG9hbWkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--multipart-after
HTTP 200


# cmd-01--gzip | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRslYoz8hPzM1Uiq2tBQCL8tXYKQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--gzip-after
HTTP 200


# cmd-01--deflate | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbJWKM/IT8zNVIqtrQUAIf8N7A==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--deflate-after
HTTP 200


# cmd-01--br | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxSAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI7IHdob2FtaSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-01--br-after
HTTP 200


# cmd-bypass-01--query | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=x%26%26%20PoWerShell%20-nop
X-Hurl-Case: cmd-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-after
HTTP 200


# cmd-bypass-01--query-double | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=x%2526%2526%2520PoWerShell%2520-nop
X-Hurl-Case: cmd-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-double-after
HTTP 200


# cmd-bypass-01--query-duplicate | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=x%26%26%20PoWerShell%20-nop
X-Hurl-Case: cmd-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--query-duplicate-after
HTTP 200


# cmd-bypass-01--header | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--header
X-Probe: x%26%26%20PoWerShell%20-nop
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--header-after
HTTP 200


# cmd-bypass-01--cookie | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--cookie
Cookie: probe=x%26%26%20PoWerShell%20-nop
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--cookie-after
HTTP 200


# cmd-bypass-01--form | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=x%26%26%20PoWerShell%20-nop
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--form-after
HTTP 200


# cmd-bypass-01--json | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","x&& PoWerShell -nop"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-after
HTTP 200


# cmd-bypass-01--json-put | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","x&& PoWerShell -nop"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-put-after
HTTP 200


# cmd-bypass-01--json-patch | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","x&& PoWerShell -nop"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--json-patch-after
HTTP 200


# cmd-bypass-01--text | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--text
Content-Type: text/plain
```
x&& PoWerShell -nop
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--text-after
HTTP 200


# cmd-bypass-01--xml | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--xml
Content-Type: application/xml
```
<root><value>x&amp;&amp; PoWerShell -nop</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--xml-after
HTTP 200


# cmd-bypass-01--multipart | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeCYmIFBvV2VyU2hlbGwgLW5vcA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--multipart-after
HTTP 200


# cmd-bypass-01--gzip | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqlBTUwjID08tCs5IzclR0M3LL1CKra0FAGEYFnw0AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--gzip-after
HTTP 200


# cmd-bypass-01--deflate | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUapQU1MIyA9PLQrOSM3JUdDNyy9Qiq2tBQDKDxFv;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--deflate-after
HTTP 200


# cmd-bypass-01--br | 命令注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: cmd-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixmAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ4JiYgUG9XZXJTaGVsbCAtbm9wIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: cmd-bypass-01--br-after
HTTP 200


# extra-cmd-pipe--query | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-before
HTTP 200

GET {{base_url}}/probe?q=value%7Cwhoami
X-Hurl-Case: extra-cmd-pipe--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-after
HTTP 200


# extra-cmd-pipe--query-double | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-double-before
HTTP 200

GET {{base_url}}/probe?q=value%257Cwhoami
X-Hurl-Case: extra-cmd-pipe--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-double-after
HTTP 200


# extra-cmd-pipe--query-duplicate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=value%7Cwhoami
X-Hurl-Case: extra-cmd-pipe--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--query-duplicate-after
HTTP 200


# extra-cmd-pipe--header | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--header
X-Probe: value%7Cwhoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--header-after
HTTP 200


# extra-cmd-pipe--cookie | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--cookie
Cookie: probe=value%7Cwhoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--cookie-after
HTTP 200


# extra-cmd-pipe--form | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--form
Content-Type: application/x-www-form-urlencoded
```
q=value%7Cwhoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--form-after
HTTP 200


# extra-cmd-pipe--json | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--json
Content-Type: application/json
```
{"data":{"values":["normal","value|whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-after
HTTP 200


# extra-cmd-pipe--json-put | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--json-put
Content-Type: application/json
```
{"data":{"values":["normal","value|whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-put-after
HTTP 200


# extra-cmd-pipe--json-patch | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","value|whoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--json-patch-after
HTTP 200


# extra-cmd-pipe--text | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--text
Content-Type: text/plain
```
value|whoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--text-after
HTTP 200


# extra-cmd-pipe--xml | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--xml
Content-Type: application/xml
```
<root><value>value|whoami</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--xml-after
HTTP 200


# extra-cmd-pipe--multipart | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KdmFsdWV8d2hvYW1pDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--multipart-after
HTTP 200


# extra-cmd-pipe--gzip | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdKBCNWUZ+Qn5mYqxdbWAgBXaLtuLQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--gzip-after
HTTP 200


# extra-cmd-pipe--deflate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSgQjVlGfkJ+ZmKsXW1gIAZx8QKg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--deflate-after
HTTP 200


# extra-cmd-pipe--br | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-pipe--br
Content-Type: application/json
Content-Encoding: br
base64,CxaAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ2YWx1ZXx3aG9hbWkiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-pipe--br-after
HTTP 200


# extra-cmd-substitution--query | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-before
HTTP 200

GET {{base_url}}/probe?q=%24(id)
X-Hurl-Case: extra-cmd-substitution--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-after
HTTP 200


# extra-cmd-substitution--query-double | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2524(id)
X-Hurl-Case: extra-cmd-substitution--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-double-after
HTTP 200


# extra-cmd-substitution--query-duplicate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%24(id)
X-Hurl-Case: extra-cmd-substitution--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--query-duplicate-after
HTTP 200


# extra-cmd-substitution--header | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--header
X-Probe: %24(id)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--header-after
HTTP 200


# extra-cmd-substitution--cookie | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--cookie
Cookie: probe=%24(id)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--cookie-after
HTTP 200


# extra-cmd-substitution--form | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--form
Content-Type: application/x-www-form-urlencoded
```
q=%24(id)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--form-after
HTTP 200


# extra-cmd-substitution--json | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--json
Content-Type: application/json
```
{"data":{"values":["normal","$(id)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-after
HTTP 200


# extra-cmd-substitution--json-put | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--json-put
Content-Type: application/json
```
{"data":{"values":["normal","$(id)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-put-after
HTTP 200


# extra-cmd-substitution--json-patch | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","$(id)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--json-patch-after
HTTP 200


# extra-cmd-substitution--text | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--text
Content-Type: text/plain
```
$(id)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--text-after
HTTP 200


# extra-cmd-substitution--xml | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--xml
Content-Type: application/xml
```
<root><value>$(id)</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--xml-after
HTTP 200


# extra-cmd-substitution--multipart | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJChpZCkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--multipart-after
HTTP 200


# extra-cmd-substitution--gzip | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUtHITNFUiq2tBQAne1LSJgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--gzip-after
HTTP 200


# extra-cmd-substitution--deflate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVLRyEzRVIqtrQUA9pMMTg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--deflate-after
HTTP 200


# extra-cmd-substitution--br | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-substitution--br
Content-Type: application/json
Content-Encoding: br
base64,ixKAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIkKGlkKSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-substitution--br-after
HTTP 200


# extra-cmd-backtick--query | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-before
HTTP 200

GET {{base_url}}/probe?q=%60id%60
X-Hurl-Case: extra-cmd-backtick--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-after
HTTP 200


# extra-cmd-backtick--query-double | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2560id%2560
X-Hurl-Case: extra-cmd-backtick--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-double-after
HTTP 200


# extra-cmd-backtick--query-duplicate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%60id%60
X-Hurl-Case: extra-cmd-backtick--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--query-duplicate-after
HTTP 200


# extra-cmd-backtick--header | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--header
X-Probe: %60id%60
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--header-after
HTTP 200


# extra-cmd-backtick--cookie | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--cookie
Cookie: probe=%60id%60
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--cookie-after
HTTP 200


# extra-cmd-backtick--form | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--form
Content-Type: application/x-www-form-urlencoded
```
q=%60id%60
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--form-after
HTTP 200


# extra-cmd-backtick--json | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--json
Content-Type: application/json
```
{"data":{"values":["normal","`id`"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-after
HTTP 200


# extra-cmd-backtick--json-put | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--json-put
Content-Type: application/json
```
{"data":{"values":["normal","`id`"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-put-after
HTTP 200


# extra-cmd-backtick--json-patch | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","`id`"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--json-patch-after
HTTP 200


# extra-cmd-backtick--text | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--text
Content-Type: text/plain
```
`id`
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--text-after
HTTP 200


# extra-cmd-backtick--xml | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--xml
Content-Type: application/xml
```
<root><value>`id`</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--xml-after
HTTP 200


# extra-cmd-backtick--multipart | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KYGlkYA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--multipart-after
HTTP 200


# extra-cmd-backtick--gzip | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSshMSVCKra0FAHchCUslAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--gzip-after
HTTP 200


# extra-cmd-backtick--deflate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrITElQiq2tBQDujwyZ;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--deflate-after
HTTP 200


# extra-cmd-backtick--br | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-backtick--br
Content-Type: application/json
Content-Encoding: br
base64,CxKAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJgaWRgIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-backtick--br-after
HTTP 200


# extra-cmd-newline--query | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-before
HTTP 200

GET {{base_url}}/probe?q=value%0Awhoami
X-Hurl-Case: extra-cmd-newline--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-after
HTTP 200


# extra-cmd-newline--query-double | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-double-before
HTTP 200

GET {{base_url}}/probe?q=value%250Awhoami
X-Hurl-Case: extra-cmd-newline--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-double-after
HTTP 200


# extra-cmd-newline--query-duplicate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=value%0Awhoami
X-Hurl-Case: extra-cmd-newline--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--query-duplicate-after
HTTP 200


# extra-cmd-newline--header | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--header
X-Probe: value%0Awhoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--header-after
HTTP 200


# extra-cmd-newline--cookie | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--cookie
Cookie: probe=value%0Awhoami
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--cookie-after
HTTP 200


# extra-cmd-newline--form | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--form
Content-Type: application/x-www-form-urlencoded
```
q=value%0Awhoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--form-after
HTTP 200


# extra-cmd-newline--json | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--json
Content-Type: application/json
```
{"data":{"values":["normal","value\nwhoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-after
HTTP 200


# extra-cmd-newline--json-put | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--json-put
Content-Type: application/json
```
{"data":{"values":["normal","value\nwhoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-put-after
HTTP 200


# extra-cmd-newline--json-patch | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","value\nwhoami"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--json-patch-after
HTTP 200


# extra-cmd-newline--text | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--text
Content-Type: text/plain
```
value
whoami
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--text-after
HTTP 200


# extra-cmd-newline--xml | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--xml
Content-Type: application/xml
```
<root><value>value
whoami</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--xml-after
HTTP 200


# extra-cmd-newline--multipart | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KdmFsdWUKd2hvYW1pDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--multipart-after
HTTP 200


# extra-cmd-newline--gzip | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdKBCMXklWfkJ+ZmKsXW1gIAC64QGy4AAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--gzip-after
HTTP 200


# extra-cmd-newline--deflate | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSgQjF5JVn5CfmZirF1tYCAHaFEHg=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--deflate-after
HTTP 200


# extra-cmd-newline--br | command | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-cmd-newline--br
Content-Type: application/json
Content-Encoding: br
base64,ixaAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ2YWx1ZVxud2hvYW1pIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-cmd-newline--br-after
HTTP 200


