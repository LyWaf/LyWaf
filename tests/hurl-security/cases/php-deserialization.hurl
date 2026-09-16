# php-deserialization regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# php-01--query | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--query-before
HTTP 200

GET {{base_url}}/probe?q=O%3A4%3A%22Test%22%3A0%3A%7B%7D
X-Hurl-Case: php-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--query-after
HTTP 200


# php-01--query-double | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=O%253A4%253A%2522Test%2522%253A0%253A%257B%257D
X-Hurl-Case: php-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--query-double-after
HTTP 200


# php-01--query-duplicate | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=O%3A4%3A%22Test%22%3A0%3A%7B%7D
X-Hurl-Case: php-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--query-duplicate-after
HTTP 200


# php-01--header | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: php-01--header
X-Probe: O%3A4%3A%22Test%22%3A0%3A%7B%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--header-after
HTTP 200


# php-01--cookie | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: php-01--cookie
Cookie: probe=O%3A4%3A%22Test%22%3A0%3A%7B%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--cookie-after
HTTP 200


# php-01--form | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--form
Content-Type: application/x-www-form-urlencoded
```
q=O%3A4%3A%22Test%22%3A0%3A%7B%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--form-after
HTTP 200


# php-01--json | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--json
Content-Type: application/json
```
{"data":{"values":["normal","O:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--json-after
HTTP 200


# php-01--json-put | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: php-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","O:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--json-put-after
HTTP 200


# php-01--json-patch | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: php-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","O:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--json-patch-after
HTTP 200


# php-01--text | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--text
Content-Type: text/plain
```
O:4:"Test":0:{}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--text-after
HTTP 200


# php-01--xml | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--xml
Content-Type: application/xml
```
<root><value>O:4:"Test":0:{}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--xml-after
HTTP 200


# php-01--multipart | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KTzo0OiJUZXN0IjowOnt9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--multipart-after
HTTP 200


# php-01--gzip | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR8rcysYpRCkktLolRsjKwqq5Viq2tBQBejvlhMgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--gzip-after
HTTP 200


# php-01--deflate | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUfK3MrGKUQpJLS6JUbIysKquVYqtrQUAoaEQOw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--deflate-after
HTTP 200


# php-01--br | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixiAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJPOjQ6XCJUZXN0XCI6MDp7fSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-01--br-after
HTTP 200


# php-bypass-01--query | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=C%3A4%3A%22Test%22%3A0%3A%7B%7D
X-Hurl-Case: php-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-after
HTTP 200


# php-bypass-01--query-double | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=C%253A4%253A%2522Test%2522%253A0%253A%257B%257D
X-Hurl-Case: php-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-double-after
HTTP 200


# php-bypass-01--query-duplicate | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=C%3A4%3A%22Test%22%3A0%3A%7B%7D
X-Hurl-Case: php-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--query-duplicate-after
HTTP 200


# php-bypass-01--header | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: php-bypass-01--header
X-Probe: C%3A4%3A%22Test%22%3A0%3A%7B%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--header-after
HTTP 200


# php-bypass-01--cookie | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: php-bypass-01--cookie
Cookie: probe=C%3A4%3A%22Test%22%3A0%3A%7B%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--cookie-after
HTTP 200


# php-bypass-01--form | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=C%3A4%3A%22Test%22%3A0%3A%7B%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--form-after
HTTP 200


# php-bypass-01--json | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","C:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-after
HTTP 200


# php-bypass-01--json-put | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: php-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","C:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-put-after
HTTP 200


# php-bypass-01--json-patch | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: php-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","C:4:\"Test\":0:{}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--json-patch-after
HTTP 200


# php-bypass-01--text | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--text
Content-Type: text/plain
```
C:4:"Test":0:{}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--text-after
HTTP 200


# php-bypass-01--xml | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--xml
Content-Type: application/xml
```
<root><value>C:4:"Test":0:{}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--xml-after
HTTP 200


# php-bypass-01--multipart | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KQzo0OiJUZXN0IjowOnt9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--multipart-after
HTTP 200


# php-bypass-01--gzip | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRcrYysYpRCkktLolRsjKwqq5Viq2tBQCTg/JWMgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--gzip-after
HTTP 200


# php-bypass-01--deflate | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUXK2MrGKUQpJLS6JUbIysKquVYqtrQUAoKUQLw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--deflate-after
HTTP 200


# php-bypass-01--br | PHP反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: php-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: php-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixiAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJDOjQ6XCJUZXN0XCI6MDp7fSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: php-bypass-01--br-after
HTTP 200


