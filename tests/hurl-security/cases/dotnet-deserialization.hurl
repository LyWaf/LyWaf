# dotnet-deserialization regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# dotnet-01--query | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-before
HTTP 200

GET {{base_url}}/probe?q=__VIEWSTATE%3DAAEAAAD%2F%2F%2F%2F%2FAQAAAAAAAAAMAgAA
X-Hurl-Case: dotnet-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-after
HTTP 200


# dotnet-01--query-double | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=__VIEWSTATE%253DAAEAAAD%252F%252F%252F%252F%252FAQAAAAAAAAAMAgAA
X-Hurl-Case: dotnet-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-double-after
HTTP 200


# dotnet-01--query-duplicate | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=__VIEWSTATE%3DAAEAAAD%2F%2F%2F%2F%2FAQAAAAAAAAAMAgAA
X-Hurl-Case: dotnet-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--query-duplicate-after
HTTP 200


# dotnet-01--header | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: dotnet-01--header
X-Probe: __VIEWSTATE%3DAAEAAAD%2F%2F%2F%2F%2FAQAAAAAAAAAMAgAA
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--header-after
HTTP 200


# dotnet-01--cookie | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: dotnet-01--cookie
Cookie: probe=__VIEWSTATE%3DAAEAAAD%2F%2F%2F%2F%2FAQAAAAAAAAAMAgAA
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--cookie-after
HTTP 200


# dotnet-01--form | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--form
Content-Type: application/x-www-form-urlencoded
```
q=__VIEWSTATE%3DAAEAAAD%2F%2F%2F%2F%2FAQAAAAAAAAAMAgAA
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--form-after
HTTP 200


# dotnet-01--json | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--json
Content-Type: application/json
```
{"data":{"values":["normal","__VIEWSTATE=AAEAAAD/////AQAAAAAAAAAMAgAA"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-after
HTTP 200


# dotnet-01--json-put | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: dotnet-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","__VIEWSTATE=AAEAAAD/////AQAAAAAAAAAMAgAA"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-put-after
HTTP 200


# dotnet-01--json-patch | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: dotnet-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","__VIEWSTATE=AAEAAAD/////AQAAAAAAAAAMAgAA"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--json-patch-after
HTTP 200


# dotnet-01--text | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--text
Content-Type: text/plain
```
__VIEWSTATE=AAEAAAD/////AQAAAAAAAAAMAgAA
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--text-after
HTTP 200


# dotnet-01--xml | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--xml
Content-Type: application/xml
```
<root><value>__VIEWSTATE=AAEAAAD/////AQAAAAAAAAAMAgAA</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--xml-after
HTTP 200


# dotnet-01--multipart | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KX19WSUVXU1RBVEU9QUFFQUFBRC8vLy8vQVFBQUFBQUFBQUFNQWdBQQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--multipart-after
HTTP 200


# dotnet-01--gzip | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRio8P83QNDw5xDHG1dXR0dXR0dNEHAcdARxjwdUx3dFSKra0FAGMagNpJAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--gzip-after
HTTP 200


# dotnet-01--deflate | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUYqPD/N0DQ8OcQxxtXV0dHV0dHTRBwHHQEcY8HVMd3RUiq2tBQBMaRXO;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--deflate-after
HTTP 200


# dotnet-01--br | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0gAiIyTTPWdj1DpUG5L/fLAPjA8uGgQ1kQWLWHRBw45soQD4VPcslKAwGTIlKTZ8CYYfsFjVC2EPZUxpRBrJB1l6ysP;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-01--br-after
HTTP 200


# dotnet-bypass-01--query | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=System.Windows.Data.ObjectDataProvider
X-Hurl-Case: dotnet-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-after
HTTP 200


# dotnet-bypass-01--query-double | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=System.Windows.Data.ObjectDataProvider
X-Hurl-Case: dotnet-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-double-after
HTTP 200


# dotnet-bypass-01--query-duplicate | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=System.Windows.Data.ObjectDataProvider
X-Hurl-Case: dotnet-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--query-duplicate-after
HTTP 200


# dotnet-bypass-01--header | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--header
X-Probe: System.Windows.Data.ObjectDataProvider
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--header-after
HTTP 200


# dotnet-bypass-01--cookie | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--cookie
Cookie: probe=System.Windows.Data.ObjectDataProvider
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--cookie-after
HTTP 200


# dotnet-bypass-01--form | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=System.Windows.Data.ObjectDataProvider
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--form-after
HTTP 200


# dotnet-bypass-01--json | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","System.Windows.Data.ObjectDataProvider"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-after
HTTP 200


# dotnet-bypass-01--json-put | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","System.Windows.Data.ObjectDataProvider"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-put-after
HTTP 200


# dotnet-bypass-01--json-patch | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","System.Windows.Data.ObjectDataProvider"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--json-patch-after
HTTP 200


# dotnet-bypass-01--text | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--text
Content-Type: text/plain
```
System.Windows.Data.ObjectDataProvider
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--text-after
HTTP 200


# dotnet-bypass-01--xml | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--xml
Content-Type: application/xml
```
<root><value>System.Windows.Data.ObjectDataProvider</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--xml-after
HTTP 200


# dotnet-bypass-01--multipart | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KU3lzdGVtLldpbmRvd3MuRGF0YS5PYmplY3REYXRhUHJvdmlkZXINCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--multipart-after
HTTP 200


# dotnet-bypass-01--gzip | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRCq4sLknN1QvPzEvJLy/Wc0ksSdTzT8pKTS4BMQOK8ssyU1KLlGJrawGa5B21RwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--gzip-after
HTTP 200


# dotnet-bypass-01--deflate | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUQquLC5JzdULz8xLyS8v1nNJLEnU80/KSk0uATEDivLLMlNSi5Ria2sBfccZnA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--deflate-after
HTTP 200


# dotnet-bypass-01--br | .NET反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: dotnet-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0YAiJwHto1860coLEdeCwrl3pZI6rB0pYhLWPbRETnGAfuwLeONAw4OuSWWJi08QcYGhbGwCopidfU3AvHRRAY=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: dotnet-bypass-01--br-after
HTTP 200


