# traversal regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# traversal-01--query | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%2F..%2F..%2F..%2Fetc%2Fpasswd
X-Hurl-Case: traversal-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--query-after
HTTP 200


# traversal-01--query-double | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%252F..%252F..%252F..%252Fetc%252Fpasswd
X-Hurl-Case: traversal-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--query-double-after
HTTP 200


# traversal-01--query-duplicate | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%2F..%2F..%2F..%2Fetc%2Fpasswd
X-Hurl-Case: traversal-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--query-duplicate-after
HTTP 200


# traversal-01--header | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: traversal-01--header
X-Probe: %2F..%2F..%2F..%2Fetc%2Fpasswd
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--header-after
HTTP 200


# traversal-01--cookie | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: traversal-01--cookie
Cookie: probe=%2F..%2F..%2F..%2Fetc%2Fpasswd
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--cookie-after
HTTP 200


# traversal-01--form | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%2F..%2F..%2F..%2Fetc%2Fpasswd
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--form-after
HTTP 200


# traversal-01--json | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--json
Content-Type: application/json
```
{"data":{"values":["normal","/../../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--json-after
HTTP 200


# traversal-01--json-put | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: traversal-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","/../../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--json-put-after
HTTP 200


# traversal-01--json-patch | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: traversal-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","/../../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--json-patch-after
HTTP 200


# traversal-01--text | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--text
Content-Type: text/plain
```
/../../../etc/passwd
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--text-after
HTTP 200


# traversal-01--xml | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--xml
Content-Type: application/xml
```
<root><value>/../../../etc/passwd</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--xml-after
HTTP 200


# traversal-01--multipart | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KLy4uLy4uLy4uL2V0Yy9wYXNzd2QNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--multipart-after
HTTP 200


# traversal-01--gzip | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0tfTg6LUkmT9gsTi4vIUpdjaWgC3gkjpNQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--gzip-after
HTTP 200


# traversal-01--deflate | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdLX04Oi1JJk/YLE4uLyFKXY2loAxmYQ2Q==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--deflate-after
HTTP 200


# traversal-01--br | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxqAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIvLi4vLi4vLi4vZXRjL3Bhc3N3ZCJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-01--br-after
HTTP 200


# traversal-bypass-01--query | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%2F%252e%252e%252f%252e%252e%252fwindows%255cwin.ini
X-Hurl-Case: traversal-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-after
HTTP 200


# traversal-bypass-01--query-double | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%252F%25252e%25252e%25252f%25252e%25252e%25252fwindows%25255cwin.ini
X-Hurl-Case: traversal-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-double-after
HTTP 200


# traversal-bypass-01--query-duplicate | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%2F%252e%252e%252f%252e%252e%252fwindows%255cwin.ini
X-Hurl-Case: traversal-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--query-duplicate-after
HTTP 200


# traversal-bypass-01--header | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--header
X-Probe: %2F%252e%252e%252f%252e%252e%252fwindows%255cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--header-after
HTTP 200


# traversal-bypass-01--cookie | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--cookie
Cookie: probe=%2F%252e%252e%252f%252e%252e%252fwindows%255cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--cookie-after
HTTP 200


# traversal-bypass-01--form | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%2F%252e%252e%252f%252e%252e%252fwindows%255cwin.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--form-after
HTTP 200


# traversal-bypass-01--json | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","/%2e%2e%2f%2e%2e%2fwindows%5cwin.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-after
HTTP 200


# traversal-bypass-01--json-put | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","/%2e%2e%2f%2e%2e%2fwindows%5cwin.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-put-after
HTTP 200


# traversal-bypass-01--json-patch | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","/%2e%2e%2f%2e%2e%2fwindows%5cwin.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--json-patch-after
HTTP 200


# traversal-bypass-01--text | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--text
Content-Type: text/plain
```
/%2e%2e%2f%2e%2e%2fwindows%5cwin.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--text-after
HTTP 200


# traversal-bypass-01--xml | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--xml
Content-Type: application/xml
```
<root><value>/%2e%2e%2f%2e%2e%2fwindows%5cwin.ini</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--xml-after
HTTP 200


# traversal-bypass-01--multipart | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KLyUyZSUyZSUyZiUyZSUyZSUyZndpbmRvd3MlNWN3aW4uaW5pDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--multipart-after
HTTP 200


# traversal-bypass-01--gzip | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0lc1SgWjNDijPDMvJb+8WNU0uTwzTy8zL1MptrYWACyD91tFAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--gzip-after
HTTP 200


# traversal-bypass-01--deflate | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdJXNUoFozQ4ozwzLyW/vFjVNLk8M08vMy9TKba2FgD5fBYp;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--deflate-after
HTTP 200


# traversal-bypass-01--br | 路径穿越 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: traversal-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0QAiKwK7KZ5u+HqVHQI470b9Eeo9BbZc9vaIiUuaerlLnVYurKvRibLxswc4/TaZCBZBp7WA9OEkOklURIUI58wYPKP6v1tAw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: traversal-bypass-01--br-after
HTTP 200


# extra-traversal-unix--query | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-before
HTTP 200

GET {{base_url}}/probe?q=..%2F..%2Fetc%2Fpasswd
X-Hurl-Case: extra-traversal-unix--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-after
HTTP 200


# extra-traversal-unix--query-double | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-double-before
HTTP 200

GET {{base_url}}/probe?q=..%252F..%252Fetc%252Fpasswd
X-Hurl-Case: extra-traversal-unix--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-double-after
HTTP 200


# extra-traversal-unix--query-duplicate | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=..%2F..%2Fetc%2Fpasswd
X-Hurl-Case: extra-traversal-unix--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--query-duplicate-after
HTTP 200


# extra-traversal-unix--header | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--header
X-Probe: ..%2F..%2Fetc%2Fpasswd
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--header-after
HTTP 200


# extra-traversal-unix--cookie | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--cookie
Cookie: probe=..%2F..%2Fetc%2Fpasswd
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--cookie-after
HTTP 200


# extra-traversal-unix--form | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--form
Content-Type: application/x-www-form-urlencoded
```
q=..%2F..%2Fetc%2Fpasswd
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--form-after
HTTP 200


# extra-traversal-unix--json | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--json
Content-Type: application/json
```
{"data":{"values":["normal","../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-after
HTTP 200


# extra-traversal-unix--json-put | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--json-put
Content-Type: application/json
```
{"data":{"values":["normal","../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-put-after
HTTP 200


# extra-traversal-unix--json-patch | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","../../etc/passwd"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--json-patch-after
HTTP 200


# extra-traversal-unix--text | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--text
Content-Type: text/plain
```
../../etc/passwd
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--text-after
HTTP 200


# extra-traversal-unix--xml | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--xml
Content-Type: application/xml
```
<root><value>../../etc/passwd</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--xml-after
HTTP 200


# extra-traversal-unix--multipart | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KLi4vLi4vZXRjL3Bhc3N3ZA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--multipart-after
HTTP 200


# extra-traversal-unix--gzip | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0tPT19PTTy1J1i9ILC4uT1GKra0FAOF/Bs8xAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--gzip-after
HTTP 200


# extra-traversal-unix--deflate | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdLT09fT008tSdYvSCwuLk9Riq2tBQCPwRAf;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--deflate-after
HTTP 200


# extra-traversal-unix--br | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-unix--br
Content-Type: application/json
Content-Encoding: br
base64,CxiAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIuLi8uLi9ldGMvcGFzc3dkIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-unix--br-after
HTTP 200


# extra-traversal-windows--query | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-before
HTTP 200

GET {{base_url}}/probe?q=..%5C..%5Cwindows%5Cwin.ini
X-Hurl-Case: extra-traversal-windows--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-after
HTTP 200


# extra-traversal-windows--query-double | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-double-before
HTTP 200

GET {{base_url}}/probe?q=..%255C..%255Cwindows%255Cwin.ini
X-Hurl-Case: extra-traversal-windows--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-double-after
HTTP 200


# extra-traversal-windows--query-duplicate | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=..%5C..%5Cwindows%5Cwin.ini
X-Hurl-Case: extra-traversal-windows--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--query-duplicate-after
HTTP 200


# extra-traversal-windows--header | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--header
X-Probe: ..%5C..%5Cwindows%5Cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--header-after
HTTP 200


# extra-traversal-windows--cookie | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--cookie
Cookie: probe=..%5C..%5Cwindows%5Cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--cookie-after
HTTP 200


# extra-traversal-windows--form | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--form
Content-Type: application/x-www-form-urlencoded
```
q=..%5C..%5Cwindows%5Cwin.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--form-after
HTTP 200


# extra-traversal-windows--json | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--json
Content-Type: application/json
```
{"data":{"values":["normal","..\\..\\windows\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-after
HTTP 200


# extra-traversal-windows--json-put | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--json-put
Content-Type: application/json
```
{"data":{"values":["normal","..\\..\\windows\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-put-after
HTTP 200


# extra-traversal-windows--json-patch | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","..\\..\\windows\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--json-patch-after
HTTP 200


# extra-traversal-windows--text | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--text
Content-Type: text/plain
```
..\..\windows\win.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--text-after
HTTP 200


# extra-traversal-windows--xml | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--xml
Content-Type: application/xml
```
<root><value>..\..\windows\win.ini</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--xml-after
HTTP 200


# extra-traversal-windows--multipart | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KLi5cLi5cd2luZG93c1x3aW4uaW5pDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--multipart-after
HTTP 200


# extra-traversal-windows--gzip | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0tOLiQHh8sy8lPzyYjBDLzMvUym2thYA/hsSVTkAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--gzip-after
HTTP 200


# extra-traversal-windows--deflate | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdLTi4kB4fLMvJT88mIwQy8zL1MptrYWACrZE7M=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--deflate-after
HTTP 200


# extra-traversal-windows--br | traversal | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-traversal-windows--br
Content-Type: application/json
Content-Encoding: br
base64,GzgAaJwHtg1762pqaBjlvqQjVHqKbO21iAtXmtRh6Yo+cMihB6CtLQ74VCUszfiQWLZhBYhVopU/U30D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-traversal-windows--br-after
HTTP 200


