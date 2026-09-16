# xxe regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# xxe-01--query | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fpasswd%27%3E%5D%3E
X-Hurl-Case: xxe-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--query-after
HTTP 200


# xxe-01--query-double | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253C!DOCTYPE%2520x%2520%255B%253C!ENTITY%2520e%2520SYSTEM%2520%2527file%253A%252F%252F%252Fetc%252Fpasswd%2527%253E%255D%253E
X-Hurl-Case: xxe-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--query-double-after
HTTP 200


# xxe-01--query-duplicate | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fpasswd%27%3E%5D%3E
X-Hurl-Case: xxe-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--query-duplicate-after
HTTP 200


# xxe-01--header | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xxe-01--header
X-Probe: %3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fpasswd%27%3E%5D%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--header-after
HTTP 200


# xxe-01--cookie | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xxe-01--cookie
Cookie: probe=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fpasswd%27%3E%5D%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--cookie-after
HTTP 200


# xxe-01--form | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fpasswd%27%3E%5D%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--form-after
HTTP 200


# xxe-01--json | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--json
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--json-after
HTTP 200


# xxe-01--json-put | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: xxe-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--json-put-after
HTTP 200


# xxe-01--json-patch | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: xxe-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--json-patch-after
HTTP 200


# xxe-01--text | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--text
Content-Type: text/plain
```
<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--text-after
HTTP 200


# xxe-01--xml | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--xml
Content-Type: application/xml
```
<root><value>&lt;!DOCTYPE x [&lt;!ENTITY e SYSTEM 'file:///etc/passwd'&gt;]&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--xml-after
HTTP 200


# xxe-01--multipart | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPCFET0NUWVBFIHggWzwhRU5USVRZIGUgU1lTVEVNICdmaWxlOi8vL2V0Yy9wYXNzd2QnPl0+DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--multipart-after
HTTP 200


# xxe-01--gzip | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRslF08XcOiQxwVahQiLZRdPUL8QyJVEhVCI4MDnH1VVBPy8xJtdLX108tSdYvSCwuLk9Rt4u1U4qtrQUAxGKX8FcAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--gzip-after
HTTP 200


# xxe-01--deflate | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbJRdPF3DokMcFWoUIi2UXT1C/EMiVRIVQiODA5x9VVQT8vMSbXS19dPLUnWL0gsLi5PUbeLtVOKra0FAKOfGwM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--deflate-after
HTTP 200


# xxe-01--br | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-01--br
Content-Type: application/json
Content-Encoding: br
base64,G1YAMETdlupZeRHeH94MFtOLnCc1NIMgOsaB+zidL3Q9yR5wUU6MkzKVSXxgQx88QwPqoKDU6lKgavfmg/ZvE3B+VET2mTKT52JLQ/4P;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-01--br-after
HTTP 200


# xxe-bypass-01--query | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%3C!doctype%20x%20%5B%3C!entity%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fhosts%27%3E%5D%3E
X-Hurl-Case: xxe-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-after
HTTP 200


# xxe-bypass-01--query-double | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253C!doctype%2520x%2520%255B%253C!entity%2520e%2520SYSTEM%2520%2527file%253A%252F%252F%252Fetc%252Fhosts%2527%253E%255D%253E
X-Hurl-Case: xxe-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-double-after
HTTP 200


# xxe-bypass-01--query-duplicate | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3C!doctype%20x%20%5B%3C!entity%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fhosts%27%3E%5D%3E
X-Hurl-Case: xxe-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--query-duplicate-after
HTTP 200


# xxe-bypass-01--header | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--header
X-Probe: %3C!doctype%20x%20%5B%3C!entity%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fhosts%27%3E%5D%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--header-after
HTTP 200


# xxe-bypass-01--cookie | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--cookie
Cookie: probe=%3C!doctype%20x%20%5B%3C!entity%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fhosts%27%3E%5D%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--cookie-after
HTTP 200


# xxe-bypass-01--form | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%3C!doctype%20x%20%5B%3C!entity%20e%20SYSTEM%20%27file%3A%2F%2F%2Fetc%2Fhosts%27%3E%5D%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--form-after
HTTP 200


# xxe-bypass-01--json | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","<!doctype x [<!entity e SYSTEM 'file:///etc/hosts'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-after
HTTP 200


# xxe-bypass-01--json-put | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<!doctype x [<!entity e SYSTEM 'file:///etc/hosts'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-put-after
HTTP 200


# xxe-bypass-01--json-patch | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<!doctype x [<!entity e SYSTEM 'file:///etc/hosts'>]>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--json-patch-after
HTTP 200


# xxe-bypass-01--text | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--text
Content-Type: text/plain
```
<!doctype x [<!entity e SYSTEM 'file:///etc/hosts'>]>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--text-after
HTTP 200


# xxe-bypass-01--xml | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--xml
Content-Type: application/xml
```
<root><value>&lt;!doctype x [&lt;!entity e SYSTEM 'file:///etc/hosts'&gt;]&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--xml-after
HTTP 200


# xxe-bypass-01--multipart | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPCFkb2N0eXBlIHggWzwhZW50aXR5IGUgU1lTVEVNICdmaWxlOi8vL2V0Yy9ob3N0cyc+XT4NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--multipart-after
HTTP 200


# xxe-bypass-01--gzip | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRslFMyU8uqSxIVahQiLZRTM0rySypVEhVCI4MDnH1VVBPy8xJtdLX108tSdbPyC8uKVa3i7VTiq2tBQDrLK71VgAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--gzip-after
HTTP 200


# xxe-bypass-01--deflate | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbJRTMlPLqksSFWoUIi2UUzNK8ksqVRIVQiODA5x9VVQT8vMSbXS19dPLUnWz8gvLilWt4u1U4qtrQUA0zscQg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--deflate-after
HTTP 200


# xxe-bypass-01--br | XXE | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xxe-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G1UAaBSh0nVuqbIXihudQCqM5AgWiwQdExxy4B5GzS/UFkmJsjT6Qwo6uXGgDQOogz4MNPlc8iZ3rXqUq03EcqmIZE2B+rCFIf4H;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xxe-bypass-01--br-after
HTTP 200


# extra-xxe-file--query | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-before
HTTP 200

GET {{base_url}}/probe?q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%22file%3A%2F%2F%2Fetc%2Fpasswd%22%3E%5D%3E%3Cx%3E%26e%3B%3C%2Fx%3E
X-Hurl-Case: extra-xxe-file--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-after
HTTP 200


# extra-xxe-file--query-double | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253C!DOCTYPE%2520x%2520%255B%253C!ENTITY%2520e%2520SYSTEM%2520%2522file%253A%252F%252F%252Fetc%252Fpasswd%2522%253E%255D%253E%253Cx%253E%2526e%253B%253C%252Fx%253E
X-Hurl-Case: extra-xxe-file--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-double-after
HTTP 200


# extra-xxe-file--query-duplicate | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%22file%3A%2F%2F%2Fetc%2Fpasswd%22%3E%5D%3E%3Cx%3E%26e%3B%3C%2Fx%3E
X-Hurl-Case: extra-xxe-file--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--query-duplicate-after
HTTP 200


# extra-xxe-file--header | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--header
X-Probe: %3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%22file%3A%2F%2F%2Fetc%2Fpasswd%22%3E%5D%3E%3Cx%3E%26e%3B%3C%2Fx%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--header-after
HTTP 200


# extra-xxe-file--cookie | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--cookie
Cookie: probe=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%22file%3A%2F%2F%2Fetc%2Fpasswd%22%3E%5D%3E%3Cx%3E%26e%3B%3C%2Fx%3E
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--cookie-after
HTTP 200


# extra-xxe-file--form | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--form
Content-Type: application/x-www-form-urlencoded
```
q=%3C!DOCTYPE%20x%20%5B%3C!ENTITY%20e%20SYSTEM%20%22file%3A%2F%2F%2Fetc%2Fpasswd%22%3E%5D%3E%3Cx%3E%26e%3B%3C%2Fx%3E
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--form-after
HTTP 200


# extra-xxe-file--json | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--json
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-after
HTTP 200


# extra-xxe-file--json-put | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-put-after
HTTP 200


# extra-xxe-file--json-patch | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--json-patch-after
HTTP 200


# extra-xxe-file--text | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--text
Content-Type: text/plain
```
<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x>&e;</x>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--text-after
HTTP 200


# extra-xxe-file--xml | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--xml
Content-Type: application/xml
```
<root><value>&lt;!DOCTYPE x [&lt;!ENTITY e SYSTEM "file:///etc/passwd"&gt;]&gt;&lt;x&gt;&amp;e;&lt;/x&gt;</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--xml-after
HTTP 200


# extra-xxe-file--multipart | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPCFET0NUWVBFIHggWzwhRU5USVRZIGUgU1lTVEVNICJmaWxlOi8vL2V0Yy9wYXNzd2QiPl0+PHg+JmU7PC94Pg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--multipart-after
HTTP 200


# extra-xxe-file--gzip | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRslF08XcOiQxwVahQiLZRdPUL8QyJVEhVCI4MDnH1VYhRSsvMSbXS19dPLUnWL0gsLi5PiVGyi7WzqbBTS7W20a+wU4qtrQUAF1S9LGMAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--gzip-after
HTTP 200


# extra-xxe-file--deflate | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbJRdPF3DokMcFWoUIi2UXT1C/EMiVRIVQiODA5x9VWIUUrLzEm10tfXTy1J1i9ILC4uT4lRsou1s6mwU0u1ttGvsFOKra0FAPx5Hoo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--deflate-after
HTTP 200


# extra-xxe-file--br | xxe | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xxe-file--br
Content-Type: application/json
Content-Encoding: br
base64,G2IAEBwJdgzlH3ESpY6QOR3KbalfMAnfx1izJDXpQlYQc8eJrkGSqLI4xgn4NN9C10SjXA0PSdES0x0kXHrwwNhDGuvgoFK1vHTVyibHTTAYhEnKl/J39iBm+I2SXMX8Hw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xxe-file--br-after
HTTP 200


