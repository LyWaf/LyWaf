# lfi regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# lfi-01--query | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%2Fproc%2Fself%2Fcmdline
X-Hurl-Case: lfi-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--query-after
HTTP 200


# lfi-01--query-double | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%252Fproc%252Fself%252Fcmdline
X-Hurl-Case: lfi-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--query-double-after
HTTP 200


# lfi-01--query-duplicate | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%2Fproc%2Fself%2Fcmdline
X-Hurl-Case: lfi-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--query-duplicate-after
HTTP 200


# lfi-01--header | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: lfi-01--header
X-Probe: %2Fproc%2Fself%2Fcmdline
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--header-after
HTTP 200


# lfi-01--cookie | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: lfi-01--cookie
Cookie: probe=%2Fproc%2Fself%2Fcmdline
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--cookie-after
HTTP 200


# lfi-01--form | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%2Fproc%2Fself%2Fcmdline
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--form-after
HTTP 200


# lfi-01--json | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--json
Content-Type: application/json
```
{"data":{"values":["normal","/proc/self/cmdline"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--json-after
HTTP 200


# lfi-01--json-put | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: lfi-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","/proc/self/cmdline"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--json-put-after
HTTP 200


# lfi-01--json-patch | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: lfi-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","/proc/self/cmdline"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--json-patch-after
HTTP 200


# lfi-01--text | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--text
Content-Type: text/plain
```
/proc/self/cmdline
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--text-after
HTTP 200


# lfi-01--xml | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--xml
Content-Type: application/xml
```
<root><value>/proc/self/cmdline</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--xml-after
HTTP 200


# lfi-01--multipart | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KL3Byb2Mvc2VsZi9jbWRsaW5lDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--multipart-after
HTTP 200


# lfi-01--gzip | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0i8oyk/WL07NSdNPzk3JycxLVYqtrQUABWJqeDMAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--gzip-after
HTTP 200


# lfi-01--deflate | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdIvKMpP1i9OzUnTT85NycnMS1WKra0FAMQ9EdM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--deflate-after
HTTP 200


# lfi-01--br | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxmAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIvcHJvYy9zZWxmL2NtZGxpbmUiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-01--br-after
HTTP 200


# lfi-bypass-01--query | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=C%3A%5C%5Cwindows%5C%5Cwin.ini
X-Hurl-Case: lfi-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-after
HTTP 200


# lfi-bypass-01--query-double | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=C%253A%255C%255Cwindows%255C%255Cwin.ini
X-Hurl-Case: lfi-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-double-after
HTTP 200


# lfi-bypass-01--query-duplicate | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=C%3A%5C%5Cwindows%5C%5Cwin.ini
X-Hurl-Case: lfi-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--query-duplicate-after
HTTP 200


# lfi-bypass-01--header | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--header
X-Probe: C%3A%5C%5Cwindows%5C%5Cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--header-after
HTTP 200


# lfi-bypass-01--cookie | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--cookie
Cookie: probe=C%3A%5C%5Cwindows%5C%5Cwin.ini
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--cookie-after
HTTP 200


# lfi-bypass-01--form | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=C%3A%5C%5Cwindows%5C%5Cwin.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--form-after
HTTP 200


# lfi-bypass-01--json | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","C:\\\\windows\\\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-after
HTTP 200


# lfi-bypass-01--json-put | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","C:\\\\windows\\\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-put-after
HTTP 200


# lfi-bypass-01--json-patch | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","C:\\\\windows\\\\win.ini"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--json-patch-after
HTTP 200


# lfi-bypass-01--text | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--text
Content-Type: text/plain
```
C:\\windows\\win.ini
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--text-after
HTTP 200


# lfi-bypass-01--xml | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--xml
Content-Type: application/xml
```
<root><value>C:\\windows\\win.ini</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--xml-after
HTTP 200


# lfi-bypass-01--multipart | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KQzpcXHdpbmRvd3NcXHdpbi5pbmkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--multipart-after
HTTP 200


# lfi-bypass-01--gzip | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRcraKiYmJKc/MS8kvL4Yy9TLzMpVia2sBA5sc/zkAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--gzip-after
HTTP 200


# lfi-bypass-01--deflate | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUXK2iomJiSnPzEvJLy+GMvUy8zKVYmtrATfpFDA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--deflate-after
HTTP 200


# lfi-bypass-01--br | 本地文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: lfi-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,GzgAACwO7DYfb1EmBlOlJ3XRH6HSW2Rva2hwpQ5LV/SliEsaOOTA6Vsc8CnwKpk6sLJOE0lEzn6ZfZBsAw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: lfi-bypass-01--br-after
HTTP 200


