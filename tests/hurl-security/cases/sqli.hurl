# sqli regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# sqli-01--query | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%27%20OR%201%3D1
X-Hurl-Case: sqli-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--query-after
HTTP 200


# sqli-01--query-double | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2527%2520OR%25201%253D1
X-Hurl-Case: sqli-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--query-double-after
HTTP 200


# sqli-01--query-duplicate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%27%20OR%201%3D1
X-Hurl-Case: sqli-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--query-duplicate-after
HTTP 200


# sqli-01--header | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-01--header
X-Probe: %27%20OR%201%3D1
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--header-after
HTTP 200


# sqli-01--cookie | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-01--cookie
Cookie: probe=%27%20OR%201%3D1
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--cookie-after
HTTP 200


# sqli-01--form | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%27%20OR%201%3D1
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--form-after
HTTP 200


# sqli-01--json | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--json
Content-Type: application/json
```
{"data":{"values":["normal","' OR 1=1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--json-after
HTTP 200


# sqli-01--json-put | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: sqli-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","' OR 1=1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--json-put-after
HTTP 200


# sqli-01--json-patch | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: sqli-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","' OR 1=1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--json-patch-after
HTTP 200


# sqli-01--text | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--text
Content-Type: text/plain
```
' OR 1=1
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--text-after
HTTP 200


# sqli-01--xml | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--xml
Content-Type: application/xml
```
<root><value>' OR 1=1</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--xml-after
HTTP 200


# sqli-01--multipart | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJyBPUiAxPTENCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--multipart-after
HTTP 200


# sqli-01--gzip | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUlfwD1IwtDVUiq2tBQB9b3nGKQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--gzip-after
HTTP 200


# sqli-01--deflate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVJX8A9SMLQ1VIqtrQUAGLkMsw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--deflate-after
HTTP 200


# sqli-01--br | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxSAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCInIE9SIDE9MSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-01--br-after
HTTP 200


# sqli-02--query | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20UNION%20SELECT%20username%2Cpassword%20FROM%20users
X-Hurl-Case: sqli-02--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--query-after
HTTP 200


# sqli-02--query-double | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520UNION%2520SELECT%2520username%252Cpassword%2520FROM%2520users
X-Hurl-Case: sqli-02--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--query-double-after
HTTP 200


# sqli-02--query-duplicate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20UNION%20SELECT%20username%2Cpassword%20FROM%20users
X-Hurl-Case: sqli-02--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--query-duplicate-after
HTTP 200


# sqli-02--header | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-02--header
X-Probe: 1%20UNION%20SELECT%20username%2Cpassword%20FROM%20users
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--header-after
HTTP 200


# sqli-02--cookie | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-02--cookie
Cookie: probe=1%20UNION%20SELECT%20username%2Cpassword%20FROM%20users
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--cookie-after
HTTP 200


# sqli-02--form | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20UNION%20SELECT%20username%2Cpassword%20FROM%20users
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--form-after
HTTP 200


# sqli-02--json | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--json
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION SELECT username,password FROM users"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--json-after
HTTP 200


# sqli-02--json-put | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: sqli-02--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION SELECT username,password FROM users"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--json-put-after
HTTP 200


# sqli-02--json-patch | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: sqli-02--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION SELECT username,password FROM users"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--json-patch-after
HTTP 200


# sqli-02--text | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--text
Content-Type: text/plain
```
1 UNION SELECT username,password FROM users
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--text-after
HTTP 200


# sqli-02--xml | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--xml
Content-Type: application/xml
```
<root><value>1 UNION SELECT username,password FROM users</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--xml-after
HTTP 200


# sqli-02--multipart | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBVTklPTiBTRUxFQ1QgdXNlcm5hbWUscGFzc3dvcmQgRlJPTSB1c2Vycw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--multipart-after
HTTP 200


# sqli-02--gzip | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlQI9fP091MIdvVxdQ5RKC1OLcpLzE3VKUgsLi7PL0pRcAvy9wULFyvF1tYCAJ2bcp9MAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--gzip-after
HTTP 200


# sqli-02--deflate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUCPXz9PdTCHb1cXUOUSgtTi3KS8xN1SlILC4uzy9KUXAL8vcFCxcrxdbWAgC9RhmL;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--deflate-after
HTTP 200


# sqli-02--br | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-02--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-02--br
Content-Type: application/json
Content-Encoding: br
base64,G0sAABwHdozjxSMI9ue1yN4Wy4rpSx2WLmjJAWu7LQ3C1w82xs6ICse3CSOQcesXRjStkBgqxAyPa0wqfe8B;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-02--br-after
HTTP 200


# sqli-03--query | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20AND%20sleep(5)
X-Hurl-Case: sqli-03--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--query-after
HTTP 200


# sqli-03--query-double | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520AND%2520sleep(5)
X-Hurl-Case: sqli-03--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--query-double-after
HTTP 200


# sqli-03--query-duplicate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20AND%20sleep(5)
X-Hurl-Case: sqli-03--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--query-duplicate-after
HTTP 200


# sqli-03--header | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-03--header
X-Probe: 1%20AND%20sleep(5)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--header-after
HTTP 200


# sqli-03--cookie | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-03--cookie
Cookie: probe=1%20AND%20sleep(5)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--cookie-after
HTTP 200


# sqli-03--form | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20AND%20sleep(5)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--form-after
HTTP 200


# sqli-03--json | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--json
Content-Type: application/json
```
{"data":{"values":["normal","1 AND sleep(5)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--json-after
HTTP 200


# sqli-03--json-put | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: sqli-03--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 AND sleep(5)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--json-put-after
HTTP 200


# sqli-03--json-patch | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: sqli-03--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 AND sleep(5)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--json-patch-after
HTTP 200


# sqli-03--text | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--text
Content-Type: text/plain
```
1 AND sleep(5)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--text-after
HTTP 200


# sqli-03--xml | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--xml
Content-Type: application/xml
```
<root><value>1 AND sleep(5)</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--xml-after
HTTP 200


# sqli-03--multipart | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBBTkQgc2xlZXAoNSkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--multipart-after
HTTP 200


# sqli-03--gzip | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlRw9HNRKM5JTS3QMNVUiq2tBQAsVyBILwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--gzip-after
HTTP 200


# sqli-03--deflate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUcPRzUSjOSU0t0DDVVIqtrQUAb/sO7w==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--deflate-after
HTTP 200


# sqli-03--br | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-03--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-03--br
Content-Type: application/json
Content-Encoding: br
base64,CxeAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxIEFORCBzbGVlcCg1KSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-03--br-after
HTTP 200


# sqli-bypass-01--query | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20UN%2F**%2FION%20ALL%20SELECT%201%2C2
X-Hurl-Case: sqli-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-after
HTTP 200


# sqli-bypass-01--query-double | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520UN%252F**%252FION%2520ALL%2520SELECT%25201%252C2
X-Hurl-Case: sqli-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-double-after
HTTP 200


# sqli-bypass-01--query-duplicate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20UN%2F**%2FION%20ALL%20SELECT%201%2C2
X-Hurl-Case: sqli-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--query-duplicate-after
HTTP 200


# sqli-bypass-01--header | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--header
X-Probe: 1%20UN%2F**%2FION%20ALL%20SELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--header-after
HTTP 200


# sqli-bypass-01--cookie | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--cookie
Cookie: probe=1%20UN%2F**%2FION%20ALL%20SELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--cookie-after
HTTP 200


# sqli-bypass-01--form | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20UN%2F**%2FION%20ALL%20SELECT%201%2C2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--form-after
HTTP 200


# sqli-bypass-01--json | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","1 UN/**/ION ALL SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-after
HTTP 200


# sqli-bypass-01--json-put | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 UN/**/ION ALL SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-put-after
HTTP 200


# sqli-bypass-01--json-patch | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 UN/**/ION ALL SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--json-patch-after
HTTP 200


# sqli-bypass-01--text | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--text
Content-Type: text/plain
```
1 UN/**/ION ALL SELECT 1,2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--text-after
HTTP 200


# sqli-bypass-01--xml | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--xml
Content-Type: application/xml
```
<root><value>1 UN/**/ION ALL SELECT 1,2</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--xml-after
HTTP 200


# sqli-bypass-01--multipart | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBVTi8qKi9JT04gQUxMIFNFTEVDVCAxLDINCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--multipart-after
HTTP 200


# sqli-bypass-01--gzip | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlQI9dPX0tL39PdTcPTxUQh29XF1DlEw1DFSiq2tBQAOD/VLOwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--gzip-after
HTTP 200


# sqli-bypass-01--deflate | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUCPXT19LS9/T3U3D08VEIdvVxdQ5RMNQxUoqtrQUAIRkRIA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--deflate-after
HTTP 200


# sqli-bypass-01--br | SQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: sqli-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,Cx2AeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxIFVOLyoqL0lPTiBBTEwgU0VMRUNUIDEsMiJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: sqli-bypass-01--br-after
HTTP 200


# extra-safeline-kong--query | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20and%202%3D2
X-Hurl-Case: extra-safeline-kong--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-after
HTTP 200


# extra-safeline-kong--query-double | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520and%25202%253D2
X-Hurl-Case: extra-safeline-kong--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-double-after
HTTP 200


# extra-safeline-kong--query-duplicate | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20and%202%3D2
X-Hurl-Case: extra-safeline-kong--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--query-duplicate-after
HTTP 200


# extra-safeline-kong--header | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--header
X-Probe: 1%20and%202%3D2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--header-after
HTTP 200


# extra-safeline-kong--cookie | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--cookie
Cookie: probe=1%20and%202%3D2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--cookie-after
HTTP 200


# extra-safeline-kong--form | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20and%202%3D2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--form-after
HTTP 200


# extra-safeline-kong--json | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--json
Content-Type: application/json
```
{"data":{"values":["normal","1 and 2=2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-after
HTTP 200


# extra-safeline-kong--json-put | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 and 2=2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-put-after
HTTP 200


# extra-safeline-kong--json-patch | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 and 2=2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--json-patch-after
HTTP 200


# extra-safeline-kong--text | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--text
Content-Type: text/plain
```
1 and 2=2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--text-after
HTTP 200


# extra-safeline-kong--xml | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--xml
Content-Type: application/xml
```
<root><value>1 and 2=2</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--xml-after
HTTP 200


# extra-safeline-kong--multipart | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBhbmQgMj0yDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--multipart-after
HTTP 200


# extra-safeline-kong--gzip | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlRIzEtRMLI1UoqtrQUABpsXuSoAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--gzip-after
HTTP 200


# extra-safeline-kong--deflate | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUSMxLUTCyNVKKra0FACkkDVE=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--deflate-after
HTTP 200


# extra-safeline-kong--br | sqli | SafeLine sdk/kong/Readme.md
GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-kong--br
Content-Type: application/json
Content-Encoding: br
base64,ixSAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxIGFuZCAyPTIiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-kong--br-after
HTTP 200


# extra-safeline-gzip--query | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-before
HTTP 200

GET {{base_url}}/probe?q=%27%20or%201%3D1%20--
X-Hurl-Case: extra-safeline-gzip--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-after
HTTP 200


# extra-safeline-gzip--query-double | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2527%2520or%25201%253D1%2520--
X-Hurl-Case: extra-safeline-gzip--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-double-after
HTTP 200


# extra-safeline-gzip--query-duplicate | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%27%20or%201%3D1%20--
X-Hurl-Case: extra-safeline-gzip--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--query-duplicate-after
HTTP 200


# extra-safeline-gzip--header | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--header
X-Probe: %27%20or%201%3D1%20--
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--header-after
HTTP 200


# extra-safeline-gzip--cookie | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--cookie
Cookie: probe=%27%20or%201%3D1%20--
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--cookie-after
HTTP 200


# extra-safeline-gzip--form | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--form
Content-Type: application/x-www-form-urlencoded
```
q=%27%20or%201%3D1%20--
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--form-after
HTTP 200


# extra-safeline-gzip--json | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--json
Content-Type: application/json
```
{"data":{"values":["normal","' or 1=1 --"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-after
HTTP 200


# extra-safeline-gzip--json-put | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--json-put
Content-Type: application/json
```
{"data":{"values":["normal","' or 1=1 --"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-put-after
HTTP 200


# extra-safeline-gzip--json-patch | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","' or 1=1 --"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--json-patch-after
HTTP 200


# extra-safeline-gzip--text | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--text
Content-Type: text/plain
```
' or 1=1 --
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--text-after
HTTP 200


# extra-safeline-gzip--xml | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--xml
Content-Type: application/xml
```
<root><value>' or 1=1 --</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--xml-after
HTTP 200


# extra-safeline-gzip--multipart | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJyBvciAxPTEgLS0NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--multipart-after
HTTP 200


# extra-safeline-gzip--gzip | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUlfIL1IwtDVU0NVViq2tBQAkdxokLAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--gzip-after
HTTP 200


# extra-safeline-gzip--deflate | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVJXyC9SMLQ1VNDVVYqtrQUAQFYNbQ==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--deflate-after
HTTP 200


# extra-safeline-gzip--br | sqli | SafeLine issue 1222
GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-safeline-gzip--br
Content-Type: application/json
Content-Encoding: br
base64,ixWAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCInIG9yIDE9MSAtLSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-safeline-gzip--br-after
HTTP 200


# extra-sql-case--query | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20UnIoN%20SeLeCt%201%2C2
X-Hurl-Case: extra-sql-case--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-after
HTTP 200


# extra-sql-case--query-double | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520UnIoN%2520SeLeCt%25201%252C2
X-Hurl-Case: extra-sql-case--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-double-after
HTTP 200


# extra-sql-case--query-duplicate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20UnIoN%20SeLeCt%201%2C2
X-Hurl-Case: extra-sql-case--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--query-duplicate-after
HTTP 200


# extra-sql-case--header | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-case--header
X-Probe: 1%20UnIoN%20SeLeCt%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--header-after
HTTP 200


# extra-sql-case--cookie | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-case--cookie
Cookie: probe=1%20UnIoN%20SeLeCt%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--cookie-after
HTTP 200


# extra-sql-case--form | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20UnIoN%20SeLeCt%201%2C2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--form-after
HTTP 200


# extra-sql-case--json | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--json
Content-Type: application/json
```
{"data":{"values":["normal","1 UnIoN SeLeCt 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-after
HTTP 200


# extra-sql-case--json-put | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-sql-case--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 UnIoN SeLeCt 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-put-after
HTTP 200


# extra-sql-case--json-patch | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-sql-case--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 UnIoN SeLeCt 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--json-patch-after
HTTP 200


# extra-sql-case--text | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--text
Content-Type: text/plain
```
1 UnIoN SeLeCt 1,2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--text-after
HTTP 200


# extra-sql-case--xml | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--xml
Content-Type: application/xml
```
<root><value>1 UnIoN SeLeCt 1,2</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--xml-after
HTTP 200


# extra-sql-case--multipart | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBVbklvTiBTZUxlQ3QgMSwyDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--multipart-after
HTTP 200


# extra-sql-case--gzip | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlQIzfPM91MITvVJdS5RMNQxUoqtrQUA9fbVNjMAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--gzip-after
HTTP 200


# extra-sql-case--deflate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUCM3zzPdTCE71SXUuUTDUMVKKra0FALATEBU=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--deflate-after
HTTP 200


# extra-sql-case--br | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-case--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-case--br
Content-Type: application/json
Content-Encoding: br
base64,CxmAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxIFVuSW9OIFNlTGVDdCAxLDIiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-case--br-after
HTTP 200


# extra-sql-comments--query | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20UNION%2F**%2FSELECT%201%2C2
X-Hurl-Case: extra-sql-comments--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-after
HTTP 200


# extra-sql-comments--query-double | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520UNION%252F**%252FSELECT%25201%252C2
X-Hurl-Case: extra-sql-comments--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-double-after
HTTP 200


# extra-sql-comments--query-duplicate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20UNION%2F**%2FSELECT%201%2C2
X-Hurl-Case: extra-sql-comments--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--query-duplicate-after
HTTP 200


# extra-sql-comments--header | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--header
X-Probe: 1%20UNION%2F**%2FSELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--header-after
HTTP 200


# extra-sql-comments--cookie | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--cookie
Cookie: probe=1%20UNION%2F**%2FSELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--cookie-after
HTTP 200


# extra-sql-comments--form | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20UNION%2F**%2FSELECT%201%2C2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--form-after
HTTP 200


# extra-sql-comments--json | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--json
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION/**/SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-after
HTTP 200


# extra-sql-comments--json-put | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION/**/SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-put-after
HTTP 200


# extra-sql-comments--json-patch | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION/**/SELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--json-patch-after
HTTP 200


# extra-sql-comments--text | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--text
Content-Type: text/plain
```
1 UNION/**/SELECT 1,2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--text-after
HTTP 200


# extra-sql-comments--xml | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--xml
Content-Type: application/xml
```
<root><value>1 UNION/**/SELECT 1,2</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--xml-after
HTTP 200


# extra-sql-comments--multipart | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBVTklPTi8qKi9TRUxFQ1QgMSwyDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--multipart-after
HTTP 200


# extra-sql-comments--gzip | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlQI9fP099PX0tIPdvVxdQ5RMNQxUoqtrQUAyA0SIjYAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--gzip-after
HTTP 200


# extra-sql-comments--deflate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUCPXz9PfT19LSD3b1cXUOUTDUMVKKra0FANNTEAc=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--deflate-after
HTTP 200


# extra-sql-comments--br | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-comments--br
Content-Type: application/json
Content-Encoding: br
base64,GzUA8J0Fdiy8lcQXiwmLDi+UWiRma2+pvGRfl0yx6MAGnIAHWoPcGHJgaYUjr3IUjVOXF+JPBg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-comments--br-after
HTTP 200


# extra-sql-newline--query | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20UNION%0ASELECT%201%2C2
X-Hurl-Case: extra-sql-newline--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-after
HTTP 200


# extra-sql-newline--query-double | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520UNION%250ASELECT%25201%252C2
X-Hurl-Case: extra-sql-newline--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-double-after
HTTP 200


# extra-sql-newline--query-duplicate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20UNION%0ASELECT%201%2C2
X-Hurl-Case: extra-sql-newline--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--query-duplicate-after
HTTP 200


# extra-sql-newline--header | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--header
X-Probe: 1%20UNION%0ASELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--header-after
HTTP 200


# extra-sql-newline--cookie | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--cookie
Cookie: probe=1%20UNION%0ASELECT%201%2C2
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--cookie-after
HTTP 200


# extra-sql-newline--form | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20UNION%0ASELECT%201%2C2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--form-after
HTTP 200


# extra-sql-newline--json | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--json
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION\nSELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-after
HTTP 200


# extra-sql-newline--json-put | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION\nSELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-put-after
HTTP 200


# extra-sql-newline--json-patch | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 UNION\nSELECT 1,2"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--json-patch-after
HTTP 200


# extra-sql-newline--text | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--text
Content-Type: text/plain
```
1 UNION
SELECT 1,2
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--text-after
HTTP 200


# extra-sql-newline--xml | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--xml
Content-Type: application/xml
```
<root><value>1 UNION
SELECT 1,2</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--xml-after
HTTP 200


# extra-sql-newline--multipart | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBVTklPTgpTRUxFQ1QgMSwyDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--multipart-after
HTTP 200


# extra-sql-newline--gzip | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlQI9fP094vJC3b1cXUOUTDUMVKKra0FAP6B+UQ0AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--gzip-after
HTTP 200


# extra-sql-newline--deflate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUCPXz9PeLyQt29XF1DlEw1DFSiq2tBQC9MhAf;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--deflate-after
HTTP 200


# extra-sql-newline--br | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-newline--br
Content-Type: application/json
Content-Encoding: br
base64,GzMA6J0FzrmcZo8GwV9dagiEWiRma291uRWLTvZxycBETsDbAnpAb4wZnTEceVWODb2n+iniTwA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-newline--br-after
HTTP 200


# extra-sql-time--query | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-before
HTTP 200

GET {{base_url}}/probe?q=1%3B%20WAITFOR%20DELAY%20%2700%3A00%3A01%27
X-Hurl-Case: extra-sql-time--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-after
HTTP 200


# extra-sql-time--query-double | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%253B%2520WAITFOR%2520DELAY%2520%252700%253A00%253A01%2527
X-Hurl-Case: extra-sql-time--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-double-after
HTTP 200


# extra-sql-time--query-duplicate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%3B%20WAITFOR%20DELAY%20%2700%3A00%3A01%27
X-Hurl-Case: extra-sql-time--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--query-duplicate-after
HTTP 200


# extra-sql-time--header | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-time--header
X-Probe: 1%3B%20WAITFOR%20DELAY%20%2700%3A00%3A01%27
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--header-after
HTTP 200


# extra-sql-time--cookie | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-time--cookie
Cookie: probe=1%3B%20WAITFOR%20DELAY%20%2700%3A00%3A01%27
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--cookie-after
HTTP 200


# extra-sql-time--form | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--form
Content-Type: application/x-www-form-urlencoded
```
q=1%3B%20WAITFOR%20DELAY%20%2700%3A00%3A01%27
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--form-after
HTTP 200


# extra-sql-time--json | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--json
Content-Type: application/json
```
{"data":{"values":["normal","1; WAITFOR DELAY '00:00:01'"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-after
HTTP 200


# extra-sql-time--json-put | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-sql-time--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1; WAITFOR DELAY '00:00:01'"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-put-after
HTTP 200


# extra-sql-time--json-patch | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-sql-time--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1; WAITFOR DELAY '00:00:01'"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--json-patch-after
HTTP 200


# extra-sql-time--text | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--text
Content-Type: text/plain
```
1; WAITFOR DELAY '00:00:01'
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--text-after
HTTP 200


# extra-sql-time--xml | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--xml
Content-Type: application/xml
```
<root><value>1; WAITFOR DELAY '00:00:01'</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--xml-after
HTTP 200


# extra-sql-time--multipart | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMTsgV0FJVEZPUiBERUxBWSAnMDA6MDA6MDEnDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--multipart-after
HTTP 200


# extra-sql-time--gzip | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMrRWCHf0DHHzD1JwcfVxjFRQNzCwAiFDdaXY2loADNcwZTwAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--gzip-after
HTTP 200


# extra-sql-time--deflate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTK0Vgh39Axx8w9ScHH1cYxUUDcwsAIhQ3Wl2NpaADYHEUY=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--deflate-after
HTTP 200


# extra-sql-time--br | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-time--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-time--br
Content-Type: application/json
Content-Encoding: br
base64,ix2AeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxOyBXQUlURk9SIERFTEFZICcwMDowMDowMSciXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-time--br-after
HTTP 200


# extra-sql-error--query | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-before
HTTP 200

GET {{base_url}}/probe?q=1%20AND%20extractvalue(1%2Cconcat(0x7e%2Cversion()))
X-Hurl-Case: extra-sql-error--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-after
HTTP 200


# extra-sql-error--query-double | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-double-before
HTTP 200

GET {{base_url}}/probe?q=1%2520AND%2520extractvalue(1%252Cconcat(0x7e%252Cversion()))
X-Hurl-Case: extra-sql-error--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-double-after
HTTP 200


# extra-sql-error--query-duplicate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=1%20AND%20extractvalue(1%2Cconcat(0x7e%2Cversion()))
X-Hurl-Case: extra-sql-error--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--query-duplicate-after
HTTP 200


# extra-sql-error--header | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-error--header
X-Probe: 1%20AND%20extractvalue(1%2Cconcat(0x7e%2Cversion()))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--header-after
HTTP 200


# extra-sql-error--cookie | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-sql-error--cookie
Cookie: probe=1%20AND%20extractvalue(1%2Cconcat(0x7e%2Cversion()))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--cookie-after
HTTP 200


# extra-sql-error--form | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--form
Content-Type: application/x-www-form-urlencoded
```
q=1%20AND%20extractvalue(1%2Cconcat(0x7e%2Cversion()))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--form-after
HTTP 200


# extra-sql-error--json | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--json
Content-Type: application/json
```
{"data":{"values":["normal","1 AND extractvalue(1,concat(0x7e,version()))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-after
HTTP 200


# extra-sql-error--json-put | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-sql-error--json-put
Content-Type: application/json
```
{"data":{"values":["normal","1 AND extractvalue(1,concat(0x7e,version()))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-put-after
HTTP 200


# extra-sql-error--json-patch | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-sql-error--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","1 AND extractvalue(1,concat(0x7e,version()))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--json-patch-after
HTTP 200


# extra-sql-error--text | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--text
Content-Type: text/plain
```
1 AND extractvalue(1,concat(0x7e,version()))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--text-after
HTTP 200


# extra-sql-error--xml | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--xml
Content-Type: application/xml
```
<root><value>1 AND extractvalue(1,concat(0x7e,version()))</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--xml-after
HTTP 200


# extra-sql-error--multipart | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KMSBBTkQgZXh0cmFjdHZhbHVlKDEsY29uY2F0KDB4N2UsdmVyc2lvbigpKSkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--multipart-after
HTTP 200


# extra-sql-error--gzip | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlRw9HNRSK0oKUpMLgHLaxjqJOfnJSeWaBhUmKfqlKUWFWfm52loamoqxdbWAgArP3OdTQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--gzip-after
HTTP 200


# extra-sql-error--deflate | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJUcPRzUUitKClKTC4By2sY6iTn5yUnlmgYVJin6pSlFhVn5udpaGpqKsXW1gIA6GwZpg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--deflate-after
HTTP 200


# extra-sql-error--br | sqli | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-sql-error--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-sql-error--br
Content-Type: application/json
Content-Encoding: br
base64,CyaAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIxIEFORCBleHRyYWN0dmFsdWUoMSxjb25jYXQoMHg3ZSx2ZXJzaW9uKCkpKSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-sql-error--br-after
HTTP 200


