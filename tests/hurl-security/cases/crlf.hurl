# crlf regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# extra-crlf--query | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-before
HTTP 200

GET {{base_url}}/probe?q=value%0D%0AX-Injected%3A%20yes
X-Hurl-Case: extra-crlf--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-after
HTTP 200


# extra-crlf--query-double | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-double-before
HTTP 200

GET {{base_url}}/probe?q=value%250D%250AX-Injected%253A%2520yes
X-Hurl-Case: extra-crlf--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-double-after
HTTP 200


# extra-crlf--query-duplicate | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=value%0D%0AX-Injected%3A%20yes
X-Hurl-Case: extra-crlf--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--query-duplicate-after
HTTP 200


# extra-crlf--header | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-crlf--header
X-Probe: value%0D%0AX-Injected%3A%20yes
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--header-after
HTTP 200


# extra-crlf--cookie | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-crlf--cookie
Cookie: probe=value%0D%0AX-Injected%3A%20yes
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--cookie-after
HTTP 200


# extra-crlf--form | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--form
Content-Type: application/x-www-form-urlencoded
```
q=value%0D%0AX-Injected%3A%20yes
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--form-after
HTTP 200


# extra-crlf--json | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--json
Content-Type: application/json
```
{"data":{"values":["normal","value\r\nX-Injected: yes"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-after
HTTP 200


# extra-crlf--json-put | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-crlf--json-put
Content-Type: application/json
```
{"data":{"values":["normal","value\r\nX-Injected: yes"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-put-after
HTTP 200


# extra-crlf--json-patch | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-crlf--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","value\r\nX-Injected: yes"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--json-patch-after
HTTP 200


# extra-crlf--text | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--text
Content-Type: text/plain
```
value
X-Injected: yes
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--text-after
HTTP 200


# extra-crlf--xml | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--xml
Content-Type: application/xml
```
<root><value>value
X-Injected: yes</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--xml-after
HTTP 200


# extra-crlf--multipart | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KdmFsdWUNClgtSW5qZWN0ZWQ6IHllcw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--multipart-after
HTTP 200


# extra-crlf--gzip | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdKBCMUUxeRF6HrmZaUml6SmWClUphYrxdbWAgDrB3i6OQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--gzip-after
HTTP 200


# extra-crlf--deflate | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSgQjFFMXkReh65mWlJpekplgpVKYWK8XW1gIAPBkUFw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--deflate-after
HTTP 200


# extra-crlf--br | crlf | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-crlf--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-crlf--br
Content-Type: application/json
Content-Encoding: br
base64,CxyAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ2YWx1ZVxyXG5YLUluamVjdGVkOiB5ZXMiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-crlf--br-after
HTTP 200


