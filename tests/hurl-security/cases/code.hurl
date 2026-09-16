# code regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# code-01--query | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%3C%3Fphp%20eval(%24_POST%5Bx%5D)%3B
X-Hurl-Case: code-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--query-after
HTTP 200


# code-01--query-double | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253C%253Fphp%2520eval(%2524_POST%255Bx%255D)%253B
X-Hurl-Case: code-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--query-double-after
HTTP 200


# code-01--query-duplicate | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3C%3Fphp%20eval(%24_POST%5Bx%5D)%3B
X-Hurl-Case: code-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--query-duplicate-after
HTTP 200


# code-01--header | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: code-01--header
X-Probe: %3C%3Fphp%20eval(%24_POST%5Bx%5D)%3B
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--header-after
HTTP 200


# code-01--cookie | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: code-01--cookie
Cookie: probe=%3C%3Fphp%20eval(%24_POST%5Bx%5D)%3B
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--cookie-after
HTTP 200


# code-01--form | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--form
Content-Type: application/x-www-form-urlencoded
base64,cT0lM0MlM0ZwaHAlMjBldmFsKCUyNF9QT1NUJTVCeCU1RCklM0I=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--form-after
HTTP 200


# code-01--json | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--json
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8P3BocCBldmFsKCRfUE9TVFt4XSk7Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--json-after
HTTP 200


# code-01--json-put | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: code-01--json-put
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8P3BocCBldmFsKCRfUE9TVFt4XSk7Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--json-put-after
HTTP 200


# code-01--json-patch | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: code-01--json-patch
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8P3BocCBldmFsKCRfUE9TVFt4XSk7Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--json-patch-after
HTTP 200


# code-01--text | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--text
Content-Type: text/plain
base64,PD9waHAgZXZhbCgkX1BPU1RbeF0pOw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--text-after
HTTP 200


# code-01--xml | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--xml
Content-Type: application/xml
base64,PHJvb3Q+PHZhbHVlPiZsdDs/cGhwIGV2YWwoJF9QT1NUW3hdKTs8L3ZhbHVlPjwvcm9vdD4=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--xml-after
HTTP 200


# code-01--multipart | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPD9waHAgZXZhbCgkX1BPU1RbeF0pOw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--multipart-after
HTTP 200


# code-01--gzip | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsrEvyChQSC1LzNFQiQ/wDw6JrojVtFaKra0FALPvG7g3AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--gzip-after
HTTP 200


# code-01--deflate | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbKxL8goUEgtS8zRUIkP8A8Oia6I1bRWiq2tBQD/3hIc;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--deflate-after
HTTP 200


# code-01--br | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxuAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8P3BocCBldmFsKCRfUE9TVFt4XSk7Il19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-01--br-after
HTTP 200


# code-bypass-01--query | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=EvAl(%24userInput)
X-Hurl-Case: code-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-after
HTTP 200


# code-bypass-01--query-double | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=EvAl(%2524userInput)
X-Hurl-Case: code-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-double-after
HTTP 200


# code-bypass-01--query-duplicate | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=EvAl(%24userInput)
X-Hurl-Case: code-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--query-duplicate-after
HTTP 200


# code-bypass-01--header | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: code-bypass-01--header
X-Probe: EvAl(%24userInput)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--header-after
HTTP 200


# code-bypass-01--cookie | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: code-bypass-01--cookie
Cookie: probe=EvAl(%24userInput)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--cookie-after
HTTP 200


# code-bypass-01--form | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--form
Content-Type: application/x-www-form-urlencoded
base64,cT1FdkFsKCUyNHVzZXJJbnB1dCk=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--form-after
HTTP 200


# code-bypass-01--json | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--json
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJFdkFsKCR1c2VySW5wdXQpIl19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-after
HTTP 200


# code-bypass-01--json-put | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: code-bypass-01--json-put
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJFdkFsKCR1c2VySW5wdXQpIl19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-put-after
HTTP 200


# code-bypass-01--json-patch | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: code-bypass-01--json-patch
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJFdkFsKCR1c2VySW5wdXQpIl19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--json-patch-after
HTTP 200


# code-bypass-01--text | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--text
Content-Type: text/plain
base64,RXZBbCgkdXNlcklucHV0KQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--text-after
HTTP 200


# code-bypass-01--xml | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--xml
Content-Type: application/xml
base64,PHJvb3Q+PHZhbHVlPkV2QWwoJHVzZXJJbnB1dCk8L3ZhbHVlPjwvcm9vdD4=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--xml-after
HTTP 200


# code-bypass-01--multipart | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KRXZBbCgkdXNlcklucHV0KQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--multipart-after
HTTP 200


# code-bypass-01--gzip | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRci1zzNFQKS1OLfLMKygt0VSKra0FAIiLQ7gxAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--gzip-after
HTTP 200


# code-bypass-01--deflate | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUXItc8zRUCktTi3yzCsoLdFUiq2tBQCcuhC4;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--deflate-after
HTTP 200


# code-bypass-01--br | 代码注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: code-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: code-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxiAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJFdkFsKCR1c2VySW5wdXQpIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: code-bypass-01--br-after
HTTP 200


