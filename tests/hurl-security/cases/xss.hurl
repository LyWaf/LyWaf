# xss regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# extra-xss-script--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E
X-Hurl-Case: extra-xss-script--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-after
HTTP 200


# extra-xss-script--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Cscript%253Ealert(1)%253C%252Fscript%253E
X-Hurl-Case: extra-xss-script--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-double-after
HTTP 200


# extra-xss-script--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Cscript%3Ealert(1)%3C%2Fscript%3E
X-Hurl-Case: extra-xss-script--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--query-duplicate-after
HTTP 200


# extra-xss-script--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-script--header
X-Probe: %3Cscript%3Ealert(1)%3C%2Fscript%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--header-after
HTTP 200


# extra-xss-script--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-script--cookie
Cookie: probe=%3Cscript%3Ealert(1)%3C%2Fscript%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--cookie-after
HTTP 200


# extra-xss-script--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Cscript%3Ealert(1)%3C%2Fscript%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--form-after
HTTP 200


# extra-xss-script--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--json
Content-Type: application/json
```
{"data":{"values":["normal","<script>alert(1)</script>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-after
HTTP 200


# extra-xss-script--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-script--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<script>alert(1)</script>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-put-after
HTTP 200


# extra-xss-script--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-script--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<script>alert(1)</script>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--json-patch-after
HTTP 200


# extra-xss-script--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--text
Content-Type: text/plain
```
<script>alert(1)</script>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--text-after
HTTP 200


# extra-xss-script--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--xml
Content-Type: application/xml
```
<root><value>&lt;script&gt;alert(1)&lt;/script&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--xml-after
HTTP 200


# extra-xss-script--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--multipart-after
HTTP 200


# extra-xss-script--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsilOLsosKLFLzEktKtEw1LTRhwooxdbWAgAlUUxmOgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--gzip-after
HTTP 200


# extra-xss-script--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbIpTi7KLCixS8xJLSrRMNS00YcKKMXW1gIARxsT8w==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--deflate-after
HTTP 200


# extra-xss-script--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-script--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-script--br
Content-Type: application/json
Content-Encoding: br
base64,GzkAEBwJdgzhxkXA8A/UIntbKbOvK6ZYdMAhBy4Hah60IwUbcCJpNuA0yQk0oZ4WPUz57XdnAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-script--br-after
HTTP 200


# extra-xss-image--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E
X-Hurl-Case: extra-xss-image--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-after
HTTP 200


# extra-xss-image--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Cimg%2520src%253Dx%2520onerror%253Dalert(1)%253E
X-Hurl-Case: extra-xss-image--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-double-after
HTTP 200


# extra-xss-image--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E
X-Hurl-Case: extra-xss-image--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--query-duplicate-after
HTTP 200


# extra-xss-image--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-image--header
X-Probe: %3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--header-after
HTTP 200


# extra-xss-image--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-image--cookie
Cookie: probe=%3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--cookie-after
HTTP 200


# extra-xss-image--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Cimg%20src%3Dx%20onerror%3Dalert(1)%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--form-after
HTTP 200


# extra-xss-image--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--json
Content-Type: application/json
```
{"data":{"values":["normal","<img src=x onerror=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-after
HTTP 200


# extra-xss-image--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-image--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<img src=x onerror=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-put-after
HTTP 200


# extra-xss-image--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-image--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<img src=x onerror=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--json-patch-after
HTTP 200


# extra-xss-image--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--text
Content-Type: text/plain
```
<img src=x onerror=alert(1)>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--text-after
HTTP 200


# extra-xss-image--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--xml
Content-Type: application/xml
```
<root><value>&lt;img src=x onerror=alert(1)&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--xml-after
HTTP 200


# extra-xss-image--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPGltZyBzcmM9eCBvbmVycm9yPWFsZXJ0KDEpPg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--multipart-after
HTTP 200


# extra-xss-image--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRssnMTVcoLkq2rVDIz0stKsovsk3MSS0q0TDUtFOKra0FAL2Tq7c9AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--gzip-after
HTTP 200


# extra-xss-image--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbLJzE1XKC5Ktq1QyM9LLSrKL7JNzEktKtEw1LRTiq2tBQCEgRTe;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--deflate-after
HTTP 200


# extra-xss-image--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-image--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-image--br
Content-Type: application/json
Content-Encoding: br
base64,GzwAECwO7IbXb1QEDF2Nyc8bapG9rZ4Z49NVLDol0lcpeLABJyAtAwe04bABJ5Jmiyy+3kAT6unH2OYNYm2WPA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-image--br-after
HTTP 200


# extra-xss-svg--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Csvg%20onload%3Dalert(1)%3E
X-Hurl-Case: extra-xss-svg--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-after
HTTP 200


# extra-xss-svg--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Csvg%2520onload%253Dalert(1)%253E
X-Hurl-Case: extra-xss-svg--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-double-after
HTTP 200


# extra-xss-svg--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Csvg%20onload%3Dalert(1)%3E
X-Hurl-Case: extra-xss-svg--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--query-duplicate-after
HTTP 200


# extra-xss-svg--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--header
X-Probe: %3Csvg%20onload%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--header-after
HTTP 200


# extra-xss-svg--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--cookie
Cookie: probe=%3Csvg%20onload%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--cookie-after
HTTP 200


# extra-xss-svg--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Csvg%20onload%3Dalert(1)%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--form-after
HTTP 200


# extra-xss-svg--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--json
Content-Type: application/json
```
{"data":{"values":["normal","<svg onload=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-after
HTTP 200


# extra-xss-svg--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<svg onload=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-put-after
HTTP 200


# extra-xss-svg--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<svg onload=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--json-patch-after
HTTP 200


# extra-xss-svg--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--text
Content-Type: text/plain
```
<svg onload=alert(1)>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--text-after
HTTP 200


# extra-xss-svg--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--xml
Content-Type: application/xml
```
<root><value>&lt;svg onload=alert(1)&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--xml-after
HTTP 200


# extra-xss-svg--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPHN2ZyBvbmxvYWQ9YWxlcnQoMSk+DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--multipart-after
HTTP 200


# extra-xss-svg--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsikuS1fIz8vJT0yxTcxJLSrRMNS0U4qtrQUAq852ljYAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--gzip-after
HTTP 200


# extra-xss-svg--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbIpLktXyM/LyU9MsU3MSS0q0TDUtFOKra0FAPiwEko=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--deflate-after
HTTP 200


# extra-xss-svg--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-svg--br
Content-Type: application/json
Content-Encoding: br
base64,ixqAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8c3ZnIG9ubG9hZD1hbGVydCgxKT4iXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-svg--br-after
HTTP 200


# extra-xss-link--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-before
HTTP 200

GET {{base_url}}/probe?q=javascript%3Aalert(1)
X-Hurl-Case: extra-xss-link--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-after
HTTP 200


# extra-xss-link--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-double-before
HTTP 200

GET {{base_url}}/probe?q=javascript%253Aalert(1)
X-Hurl-Case: extra-xss-link--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-double-after
HTTP 200


# extra-xss-link--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=javascript%3Aalert(1)
X-Hurl-Case: extra-xss-link--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--query-duplicate-after
HTTP 200


# extra-xss-link--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-link--header
X-Probe: javascript%3Aalert(1)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--header-after
HTTP 200


# extra-xss-link--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-link--cookie
Cookie: probe=javascript%3Aalert(1)
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--cookie-after
HTTP 200


# extra-xss-link--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--form
Content-Type: application/x-www-form-urlencoded
```
q=javascript%3Aalert(1)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--form-after
HTTP 200


# extra-xss-link--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--json
Content-Type: application/json
```
{"data":{"values":["normal","javascript:alert(1)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-after
HTTP 200


# extra-xss-link--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-link--json-put
Content-Type: application/json
```
{"data":{"values":["normal","javascript:alert(1)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-put-after
HTTP 200


# extra-xss-link--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-link--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","javascript:alert(1)"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--json-patch-after
HTTP 200


# extra-xss-link--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--text
Content-Type: text/plain
```
javascript:alert(1)
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--text-after
HTTP 200


# extra-xss-link--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--xml
Content-Type: application/xml
```
<root><value>javascript:alert(1)</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--xml-after
HTTP 200


# extra-xss-link--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KamF2YXNjcmlwdDphbGVydCgxKQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--multipart-after
HTTP 200


# extra-xss-link--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRykosSyxOLsosKLFKzEktKtEw1FSKra0FAGLkgJA0AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--gzip-after
HTTP 200


# extra-xss-link--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcpKLEssTi7KLCixSsxJLSrRMNRUiq2tBQDcjBIX;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--deflate-after
HTTP 200


# extra-xss-link--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-link--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-link--br
Content-Type: application/json
Content-Encoding: br
base64,GzMA6B0JdgzhxkXAsC/UIntbKbOvK6ZYdGADDlwCCjigjW6MGZ0jhkyVZ4+FPJhmAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-link--br-after
HTTP 200


# extra-xss-event--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Cinput%20autofocus%20onfocus%3Dalert(1)%3E
X-Hurl-Case: extra-xss-event--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-after
HTTP 200


# extra-xss-event--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Cinput%2520autofocus%2520onfocus%253Dalert(1)%253E
X-Hurl-Case: extra-xss-event--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-double-after
HTTP 200


# extra-xss-event--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Cinput%20autofocus%20onfocus%3Dalert(1)%3E
X-Hurl-Case: extra-xss-event--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--query-duplicate-after
HTTP 200


# extra-xss-event--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-event--header
X-Probe: %3Cinput%20autofocus%20onfocus%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--header-after
HTTP 200


# extra-xss-event--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-event--cookie
Cookie: probe=%3Cinput%20autofocus%20onfocus%3Dalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--cookie-after
HTTP 200


# extra-xss-event--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Cinput%20autofocus%20onfocus%3Dalert(1)%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--form-after
HTTP 200


# extra-xss-event--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--json
Content-Type: application/json
```
{"data":{"values":["normal","<input autofocus onfocus=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-after
HTTP 200


# extra-xss-event--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-event--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<input autofocus onfocus=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-put-after
HTTP 200


# extra-xss-event--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-event--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<input autofocus onfocus=alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--json-patch-after
HTTP 200


# extra-xss-event--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--text
Content-Type: text/plain
```
<input autofocus onfocus=alert(1)>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--text-after
HTTP 200


# extra-xss-event--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--xml
Content-Type: application/xml
```
<root><value>&lt;input autofocus onfocus=alert(1)&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--xml-after
HTTP 200


# extra-xss-event--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPGlucHV0IGF1dG9mb2N1cyBvbmZvY3VzPWFsZXJ0KDEpPg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--multipart-after
HTTP 200


# extra-xss-event--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRssnMKygtUUgsLclPy08uLVbIzwPTtok5qUUlGoaadkqxtbUAOVpMF0MAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--gzip-after
HTTP 200


# extra-xss-event--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbLJzCsoLVFILC3JT8tPLi1WyM8D07aJOalFJRqGmnZKsbW1ABxfF6M=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--deflate-after
HTTP 200


# extra-xss-event--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-event--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-event--br
Content-Type: application/json
Content-Encoding: br
base64,G0IAACwK7AbtjnRamE2PXUdLqEX23rZ2SoxPmOLpKhadEunzgFvR4ZxYokQBj+FzrMQjHwdqQuqoAyOCde+7HbAvKzk=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-event--br-after
HTTP 200


# extra-xss-iframe--query | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Ciframe%20src%3Djavascript%3Aalert(1)%3E
X-Hurl-Case: extra-xss-iframe--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-after
HTTP 200


# extra-xss-iframe--query-double | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Ciframe%2520src%253Djavascript%253Aalert(1)%253E
X-Hurl-Case: extra-xss-iframe--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-double-after
HTTP 200


# extra-xss-iframe--query-duplicate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Ciframe%20src%3Djavascript%3Aalert(1)%3E
X-Hurl-Case: extra-xss-iframe--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--query-duplicate-after
HTTP 200


# extra-xss-iframe--header | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--header
X-Probe: %3Ciframe%20src%3Djavascript%3Aalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--header-after
HTTP 200


# extra-xss-iframe--cookie | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--cookie
Cookie: probe=%3Ciframe%20src%3Djavascript%3Aalert(1)%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--cookie-after
HTTP 200


# extra-xss-iframe--form | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Ciframe%20src%3Djavascript%3Aalert(1)%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--form-after
HTTP 200


# extra-xss-iframe--json | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--json
Content-Type: application/json
```
{"data":{"values":["normal","<iframe src=javascript:alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-after
HTTP 200


# extra-xss-iframe--json-put | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<iframe src=javascript:alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-put-after
HTTP 200


# extra-xss-iframe--json-patch | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<iframe src=javascript:alert(1)>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--json-patch-after
HTTP 200


# extra-xss-iframe--text | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--text
Content-Type: text/plain
```
<iframe src=javascript:alert(1)>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--text-after
HTTP 200


# extra-xss-iframe--xml | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--xml
Content-Type: application/xml
```
<root><value>&lt;iframe src=javascript:alert(1)&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--xml-after
HTTP 200


# extra-xss-iframe--multipart | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPGlmcmFtZSBzcmM9amF2YXNjcmlwdDphbGVydCgxKT4NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--multipart-after
HTTP 200


# extra-xss-iframe--gzip | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsslMK0rMTVUoLkq2zUosSyxOLsosKLFKzEktKtEw1LRTiq2tBQCjvVrcQQAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--gzip-after
HTTP 200


# extra-xss-iframe--deflate | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbLJTCtKzE1VKC5Kts1KLEssTi7KLCixSsxJLSrRMNS0U4qtrQUA5cIWqg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--deflate-after
HTTP 200


# extra-xss-iframe--br | xss | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xss-iframe--br
Content-Type: application/json
Content-Encoding: br
base64,G0AAEBwH7r4L9HC7wOhoCbXI3lbfTonx6VIsOrABBy4BDRzQQIsODjmR6y2RlngJNKGOPm5i11KnJf8=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xss-iframe--br-after
HTTP 200


