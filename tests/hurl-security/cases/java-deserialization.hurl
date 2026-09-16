# java-deserialization regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# java-01--query | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--query-before
HTTP 200

GET {{base_url}}/probe?q=aced000573720011java.util.HashMap
X-Hurl-Case: java-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--query-after
HTTP 200


# java-01--query-double | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=aced000573720011java.util.HashMap
X-Hurl-Case: java-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--query-double-after
HTTP 200


# java-01--query-duplicate | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=aced000573720011java.util.HashMap
X-Hurl-Case: java-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--query-duplicate-after
HTTP 200


# java-01--header | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: java-01--header
X-Probe: aced000573720011java.util.HashMap
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--header-after
HTTP 200


# java-01--cookie | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: java-01--cookie
Cookie: probe=aced000573720011java.util.HashMap
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--cookie-after
HTTP 200


# java-01--form | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--form
Content-Type: application/x-www-form-urlencoded
```
q=aced000573720011java.util.HashMap
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--form-after
HTTP 200


# java-01--json | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--json
Content-Type: application/json
```
{"data":{"values":["normal","aced000573720011java.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--json-after
HTTP 200


# java-01--json-put | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: java-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","aced000573720011java.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--json-put-after
HTTP 200


# java-01--json-patch | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: java-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","aced000573720011java.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--json-patch-after
HTTP 200


# java-01--text | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--text
Content-Type: text/plain
```
aced000573720011java.util.HashMap
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--text-after
HTTP 200


# java-01--xml | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--xml
Content-Type: application/xml
```
<root><value>aced000573720011java.util.HashMap</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--xml-after
HTTP 200


# java-01--multipart | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KYWNlZDAwMDU3MzcyMDAxMWphdmEudXRpbC5IYXNoTWFwDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--multipart-after
HTTP 200


# java-01--gzip | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSkxOTTEwMDA1NzY3MjAwNMxKLEvUKy3JzNHzSCzO8E0sUIqtrQUAgCkjnUIAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--gzip-after
HTTP 200


# java-01--deflate | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUpMTk0xMDAwNTc2NzIwMDTMSixL1CstyczR80gszvBNLFCKra0FAMUcFVE=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--deflate-after
HTTP 200


# java-01--br | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0EA+I2TTPWdZ3lL/bLZYWYTupBM+CEwQUARfS0JKbJopDHIAQ6H8IGIH4mY4YOC1N5I+uCCJY2Z8kTfM5Z+VpNsFA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-01--br-after
HTTP 200


# java-bypass-01--query | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=rO0ABXNQAD7WTABDjava.util.HashMap
X-Hurl-Case: java-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-after
HTTP 200


# java-bypass-01--query-double | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=rO0ABXNQAD7WTABDjava.util.HashMap
X-Hurl-Case: java-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-double-after
HTTP 200


# java-bypass-01--query-duplicate | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=rO0ABXNQAD7WTABDjava.util.HashMap
X-Hurl-Case: java-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--query-duplicate-after
HTTP 200


# java-bypass-01--header | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: java-bypass-01--header
X-Probe: rO0ABXNQAD7WTABDjava.util.HashMap
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--header-after
HTTP 200


# java-bypass-01--cookie | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: java-bypass-01--cookie
Cookie: probe=rO0ABXNQAD7WTABDjava.util.HashMap
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--cookie-after
HTTP 200


# java-bypass-01--form | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=rO0ABXNQAD7WTABDjava.util.HashMap
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--form-after
HTTP 200


# java-bypass-01--json | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","rO0ABXNQAD7WTABDjava.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-after
HTTP 200


# java-bypass-01--json-put | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: java-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","rO0ABXNQAD7WTABDjava.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-put-after
HTTP 200


# java-bypass-01--json-patch | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: java-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","rO0ABXNQAD7WTABDjava.util.HashMap"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--json-patch-after
HTTP 200


# java-bypass-01--text | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--text
Content-Type: text/plain
```
rO0ABXNQAD7WTABDjava.util.HashMap
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--text-after
HTTP 200


# java-bypass-01--xml | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--xml
Content-Type: application/xml
```
<root><value>rO0ABXNQAD7WTABDjava.util.HashMap</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--xml-after
HTTP 200


# java-bypass-01--multipart | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0Kck8wQUJYTlFBRDdXVEFCRGphdmEudXRpbC5IYXNoTWFwDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--multipart-after
HTTP 200


# java-bypass-01--gzip | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRKvI3cHSK8At0dDEPD3F0cslKLEvUKy3JzNHzSCzO8E0sUIqtrQUAv8UiiEIAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--gzip-after
HTTP 200


# java-bypass-01--deflate | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUSryN3B0ivALdHQxDw9xdHLJSixL1CstyczR80gszvBNLFCKra0FANYdFgM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--deflate-after
HTTP 200


# java-bypass-01--br | Java反序列化 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: java-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: java-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0EA+I2TVOU9z8Br5MdfVrbttRM7SqSvJSFFFo00BjnA4RB+ILRfQsv4wGGzJEJj0yG6HPmE6l1zNcJ5tIPwrBrNgmI=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: java-bypass-01--br-after
HTTP 200


