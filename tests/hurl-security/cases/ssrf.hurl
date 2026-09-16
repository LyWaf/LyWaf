# ssrf regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# ssrf-01--query | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F127.0.0.1%3A8080%2Fadmin
X-Hurl-Case: ssrf-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-after
HTTP 200


# ssrf-01--query-double | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F127.0.0.1%253A8080%252Fadmin
X-Hurl-Case: ssrf-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-double-after
HTTP 200


# ssrf-01--query-duplicate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F127.0.0.1%3A8080%2Fadmin
X-Hurl-Case: ssrf-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--query-duplicate-after
HTTP 200


# ssrf-01--header | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-01--header
X-Probe: http%3A%2F%2F127.0.0.1%3A8080%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--header-after
HTTP 200


# ssrf-01--cookie | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-01--cookie
Cookie: probe=http%3A%2F%2F127.0.0.1%3A8080%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--cookie-after
HTTP 200


# ssrf-01--form | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F127.0.0.1%3A8080%2Fadmin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--form-after
HTTP 200


# ssrf-01--json | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--json
Content-Type: application/json
```
{"data":{"values":["normal","http://127.0.0.1:8080/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-after
HTTP 200


# ssrf-01--json-put | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: ssrf-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://127.0.0.1:8080/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-put-after
HTTP 200


# ssrf-01--json-patch | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: ssrf-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://127.0.0.1:8080/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--json-patch-after
HTTP 200


# ssrf-01--text | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--text
Content-Type: text/plain
```
http://127.0.0.1:8080/admin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--text-after
HTTP 200


# ssrf-01--xml | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--xml
Content-Type: application/xml
```
<root><value>http://127.0.0.1:8080/admin</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--xml-after
HTTP 200


# ssrf-01--multipart | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovLzEyNy4wLjAuMTo4MDgwL2FkbWluDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--multipart-after
HTTP 200


# ssrf-01--gzip | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS1zc0Mtcz0DPQM7SyMLAw0E9Myc3MU4qtrQUAtNmQujwAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--gzip-after
HTTP 200


# ssrf-01--deflate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tc3NDLXM9Az0DO0sjCwMNBPTMnNzFOKra0FAEOkEls=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--deflate-after
HTTP 200


# ssrf-01--br | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-01--br
Content-Type: application/json
Content-Encoding: br
base64,GzsAiJwHto186yoVCkv3KIxQ6Smytechw9i8wqQOS1f0gUOONDDP8qRdKgOmQyYXTRZMgMDK03cKkT+ZDTM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-01--br-after
HTTP 200


# ssrf-02--query | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-before
HTTP 200

GET {{base_url}}/probe?q=gopher%3A%2F%2Flocalhost%3A6379%2F_PING
X-Hurl-Case: ssrf-02--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-after
HTTP 200


# ssrf-02--query-double | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-double-before
HTTP 200

GET {{base_url}}/probe?q=gopher%253A%252F%252Flocalhost%253A6379%252F_PING
X-Hurl-Case: ssrf-02--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-double-after
HTTP 200


# ssrf-02--query-duplicate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=gopher%3A%2F%2Flocalhost%3A6379%2F_PING
X-Hurl-Case: ssrf-02--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--query-duplicate-after
HTTP 200


# ssrf-02--header | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-02--header
X-Probe: gopher%3A%2F%2Flocalhost%3A6379%2F_PING
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--header-after
HTTP 200


# ssrf-02--cookie | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-02--cookie
Cookie: probe=gopher%3A%2F%2Flocalhost%3A6379%2F_PING
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--cookie-after
HTTP 200


# ssrf-02--form | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--form
Content-Type: application/x-www-form-urlencoded
```
q=gopher%3A%2F%2Flocalhost%3A6379%2F_PING
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--form-after
HTTP 200


# ssrf-02--json | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--json
Content-Type: application/json
```
{"data":{"values":["normal","gopher://localhost:6379/_PING"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-after
HTTP 200


# ssrf-02--json-put | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: ssrf-02--json-put
Content-Type: application/json
```
{"data":{"values":["normal","gopher://localhost:6379/_PING"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-put-after
HTTP 200


# ssrf-02--json-patch | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: ssrf-02--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","gopher://localhost:6379/_PING"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--json-patch-after
HTTP 200


# ssrf-02--text | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--text
Content-Type: text/plain
```
gopher://localhost:6379/_PING
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--text-after
HTTP 200


# ssrf-02--xml | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--xml
Content-Type: application/xml
```
<root><value>gopher://localhost:6379/_PING</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--xml-after
HTTP 200


# ssrf-02--multipart | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KZ29waGVyOi8vbG9jYWxob3N0OjYzNzkvX1BJTkcNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--multipart-after
HTTP 200


# ssrf-02--gzip | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSs8vyEgtstLXz8lPTszJyC8usTIzNrfUjw/w9HNXiq2tBQCIegHZPgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--gzip-after
HTTP 200


# ssrf-02--deflate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrPL8hILbLS18/JT07MycgvLrEyMza31I8P8PRzV4qtrQUAloAUwQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--deflate-after
HTTP 200


# ssrf-02--br | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-02--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-02--br
Content-Type: application/json
Content-Encoding: br
base64,ix6AeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJnb3BoZXI6Ly9sb2NhbGhvc3Q6NjM3OS9fUElORyJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-02--br-after
HTTP 200


# ssrf-bypass-01--query | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
X-Hurl-Case: ssrf-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-after
HTTP 200


# ssrf-bypass-01--query-double | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F169.254.169.254%252Flatest%252Fmeta-data%252F
X-Hurl-Case: ssrf-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-double-after
HTTP 200


# ssrf-bypass-01--query-duplicate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
X-Hurl-Case: ssrf-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--query-duplicate-after
HTTP 200


# ssrf-bypass-01--header | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--header
X-Probe: http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--header-after
HTTP 200


# ssrf-bypass-01--cookie | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--cookie
Cookie: probe=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--cookie-after
HTTP 200


# ssrf-bypass-01--form | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--form-after
HTTP 200


# ssrf-bypass-01--json | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-after
HTTP 200


# ssrf-bypass-01--json-put | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-put-after
HTTP 200


# ssrf-bypass-01--json-patch | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--json-patch-after
HTTP 200


# ssrf-bypass-01--text | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--text
Content-Type: text/plain
```
http://169.254.169.254/latest/meta-data/
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--text-after
HTTP 200


# ssrf-bypass-01--xml | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--xml
Content-Type: application/xml
```
<root><value>http://169.254.169.254/latest/meta-data/</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--xml-after
HTTP 200


# ssrf-bypass-01--multipart | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovLzE2OS4yNTQuMTY5LjI1NC9sYXRlc3QvbWV0YS1kYXRhLw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--multipart-after
HTTP 200


# ssrf-bypass-01--gzip | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS1zc0s9QzMjXRg9L6OYklqcUl+rmpJYm6IN36SrG1tQBT9s4ZSQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--gzip-after
HTTP 200


# ssrf-bypass-01--deflate | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tc3NLPUMzI10YPS+jmJJanFJfq5qSWJuiDd+kqxtbUAUgkW7A==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--deflate-after
HTTP 200


# ssrf-bypass-01--br | SSRF | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ssrf-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0gAgAUib6lf5n8Mm0/VBEHUtN/DBpyATPsWjfvgIQvtWcYkdgTmFIMtHCFxz1fGlNqbIB01+u4/OVVlRwA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ssrf-bypass-01--br-after
HTTP 200


# extra-ssrf-ipv6--query | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F%5B%3A%3A1%5D%2Fadmin
X-Hurl-Case: extra-ssrf-ipv6--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-after
HTTP 200


# extra-ssrf-ipv6--query-double | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F%255B%253A%253A1%255D%252Fadmin
X-Hurl-Case: extra-ssrf-ipv6--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-double-after
HTTP 200


# extra-ssrf-ipv6--query-duplicate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F%5B%3A%3A1%5D%2Fadmin
X-Hurl-Case: extra-ssrf-ipv6--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--query-duplicate-after
HTTP 200


# extra-ssrf-ipv6--header | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--header
X-Probe: http%3A%2F%2F%5B%3A%3A1%5D%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--header-after
HTTP 200


# extra-ssrf-ipv6--cookie | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--cookie
Cookie: probe=http%3A%2F%2F%5B%3A%3A1%5D%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--cookie-after
HTTP 200


# extra-ssrf-ipv6--form | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F%5B%3A%3A1%5D%2Fadmin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--form-after
HTTP 200


# extra-ssrf-ipv6--json | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--json
Content-Type: application/json
```
{"data":{"values":["normal","http://[::1]/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-after
HTTP 200


# extra-ssrf-ipv6--json-put | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://[::1]/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-put-after
HTTP 200


# extra-ssrf-ipv6--json-patch | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://[::1]/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--json-patch-after
HTTP 200


# extra-ssrf-ipv6--text | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--text
Content-Type: text/plain
```
http://[::1]/admin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--text-after
HTTP 200


# extra-ssrf-ipv6--xml | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--xml
Content-Type: application/xml
```
<root><value>http://[::1]/admin</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--xml-after
HTTP 200


# extra-ssrf-ipv6--multipart | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovL1s6OjFdL2FkbWluDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--multipart-after
HTTP 200


# extra-ssrf-ipv6--gzip | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS14+2sjKM1U9Myc3MU4qtrQUAMSETfTMAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--gzip-after
HTTP 200


# extra-ssrf-ipv6--deflate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tePtrIyjNVPTMnNzFOKra0FALo8EPk=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--deflate-after
HTTP 200


# extra-ssrf-ipv6--br | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-ipv6--br
Content-Type: application/json
Content-Encoding: br
base64,GzIAiIyULmsOI1Q66MZSXxbEulTPUzL6YCIn4HnyBfbAdMjUouERPkIYoAxPX/S6+aM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-ipv6--br-after
HTTP 200


# extra-ssrf-integer--query | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F2130706433%2Fadmin
X-Hurl-Case: extra-ssrf-integer--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-after
HTTP 200


# extra-ssrf-integer--query-double | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F2130706433%252Fadmin
X-Hurl-Case: extra-ssrf-integer--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-double-after
HTTP 200


# extra-ssrf-integer--query-duplicate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F2130706433%2Fadmin
X-Hurl-Case: extra-ssrf-integer--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--query-duplicate-after
HTTP 200


# extra-ssrf-integer--header | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--header
X-Probe: http%3A%2F%2F2130706433%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--header-after
HTTP 200


# extra-ssrf-integer--cookie | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--cookie
Cookie: probe=http%3A%2F%2F2130706433%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--cookie-after
HTTP 200


# extra-ssrf-integer--form | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F2130706433%2Fadmin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--form-after
HTTP 200


# extra-ssrf-integer--json | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--json
Content-Type: application/json
```
{"data":{"values":["normal","http://2130706433/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-after
HTTP 200


# extra-ssrf-integer--json-put | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://2130706433/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-put-after
HTTP 200


# extra-ssrf-integer--json-patch | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://2130706433/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--json-patch-after
HTTP 200


# extra-ssrf-integer--text | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--text
Content-Type: text/plain
```
http://2130706433/admin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--text-after
HTTP 200


# extra-ssrf-integer--xml | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--xml
Content-Type: application/xml
```
<root><value>http://2130706433/admin</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--xml-after
HTTP 200


# extra-ssrf-integer--multipart | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovLzIxMzA3MDY0MzMvYWRtaW4NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--multipart-after
HTTP 200


# extra-ssrf-integer--gzip | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS1zcyNDYwNzAzMTbWT0zJzcxTiq2tBQBnDv0SOAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--gzip-after
HTTP 200


# extra-ssrf-integer--deflate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tc3MjQ2MDcwMzE21k9Myc3MU4qtrQUAAuIRmQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--deflate-after
HTTP 200


# extra-ssrf-integer--br | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-integer--br
Content-Type: application/json
Content-Encoding: br
base64,GzcAiJwFdiwPWxoMnnW3hREqPUW29sBTPE+obKnD0hV9MJET8Dz5ArIHpkMmFw0TEyJQefo1+H0DFcw=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-integer--br-after
HTTP 200


# extra-ssrf-hex--query | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F0x7f000001%2Fadmin
X-Hurl-Case: extra-ssrf-hex--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-after
HTTP 200


# extra-ssrf-hex--query-double | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F0x7f000001%252Fadmin
X-Hurl-Case: extra-ssrf-hex--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-double-after
HTTP 200


# extra-ssrf-hex--query-duplicate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F0x7f000001%2Fadmin
X-Hurl-Case: extra-ssrf-hex--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--query-duplicate-after
HTTP 200


# extra-ssrf-hex--header | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--header
X-Probe: http%3A%2F%2F0x7f000001%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--header-after
HTTP 200


# extra-ssrf-hex--cookie | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--cookie
Cookie: probe=http%3A%2F%2F0x7f000001%2Fadmin
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--cookie-after
HTTP 200


# extra-ssrf-hex--form | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F0x7f000001%2Fadmin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--form-after
HTTP 200


# extra-ssrf-hex--json | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--json
Content-Type: application/json
```
{"data":{"values":["normal","http://0x7f000001/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-after
HTTP 200


# extra-ssrf-hex--json-put | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://0x7f000001/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-put-after
HTTP 200


# extra-ssrf-hex--json-patch | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://0x7f000001/admin"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--json-patch-after
HTTP 200


# extra-ssrf-hex--text | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--text
Content-Type: text/plain
```
http://0x7f000001/admin
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--text-after
HTTP 200


# extra-ssrf-hex--xml | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--xml
Content-Type: application/xml
```
<root><value>http://0x7f000001/admin</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--xml-after
HTTP 200


# extra-ssrf-hex--multipart | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovLzB4N2YwMDAwMDEvYWRtaW4NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--multipart-after
HTTP 200


# extra-ssrf-hex--gzip | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS1zeoME8zAAFD/cSU3Mw8pdjaWgD9J4NHOAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--gzip-after
HTTP 200


# extra-ssrf-hex--deflate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tc3qDBPMwABQ/3ElNzMPKXY2loACqsSAg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--deflate-after
HTTP 200


# extra-ssrf-hex--br | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-hex--br
Content-Type: application/json
Content-Encoding: br
base64,GzcAiCwK7FgYKa6mZkrGySpnhEpPka29grlJHZau7MMzq5cYcMgJ+OOknSoEpkMmFw1LJkhg5el7pIIZ;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-hex--br-after
HTTP 200


# extra-ssrf-metadata--query | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-before
HTTP 200

GET {{base_url}}/probe?q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
X-Hurl-Case: extra-ssrf-metadata--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-after
HTTP 200


# extra-ssrf-metadata--query-double | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-double-before
HTTP 200

GET {{base_url}}/probe?q=http%253A%252F%252F169.254.169.254%252Flatest%252Fmeta-data%252F
X-Hurl-Case: extra-ssrf-metadata--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-double-after
HTTP 200


# extra-ssrf-metadata--query-duplicate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
X-Hurl-Case: extra-ssrf-metadata--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--query-duplicate-after
HTTP 200


# extra-ssrf-metadata--header | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--header
X-Probe: http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--header-after
HTTP 200


# extra-ssrf-metadata--cookie | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--cookie
Cookie: probe=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--cookie-after
HTTP 200


# extra-ssrf-metadata--form | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--form
Content-Type: application/x-www-form-urlencoded
```
q=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data%2F
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--form-after
HTTP 200


# extra-ssrf-metadata--json | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--json
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-after
HTTP 200


# extra-ssrf-metadata--json-put | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--json-put
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-put-after
HTTP 200


# extra-ssrf-metadata--json-patch | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","http://169.254.169.254/latest/meta-data/"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--json-patch-after
HTTP 200


# extra-ssrf-metadata--text | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--text
Content-Type: text/plain
```
http://169.254.169.254/latest/meta-data/
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--text-after
HTTP 200


# extra-ssrf-metadata--xml | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--xml
Content-Type: application/xml
```
<root><value>http://169.254.169.254/latest/meta-data/</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--xml-after
HTTP 200


# extra-ssrf-metadata--multipart | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cDovLzE2OS4yNTQuMTY5LjI1NC9sYXRlc3QvbWV0YS1kYXRhLw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--multipart-after
HTTP 200


# extra-ssrf-metadata--gzip | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKbDS1zc0s9QzMjXRg9L6OYklqcUl+rmpJYm6IN36SrG1tQBT9s4ZSQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--gzip-after
HTTP 200


# extra-ssrf-metadata--deflate | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSmw0tc3NLPUMzI10YPS+jmJJanFJfq5qSWJuiDd+kqxtbUAUgkW7A==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--deflate-after
HTTP 200


# extra-ssrf-metadata--br | ssrf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ssrf-metadata--br
Content-Type: application/json
Content-Encoding: br
base64,G0gAgAUib6lf5n8Mm0/VBEHUtN/DBpyATPsWjfvgIQvtWcYkdgTmFIMtHCFxz1fGlNqbIB01+u4/OVVlRwA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ssrf-metadata--br-after
HTTP 200


