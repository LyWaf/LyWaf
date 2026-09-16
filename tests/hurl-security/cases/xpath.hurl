# xpath regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# xpath-01--query | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%27%20or%20%271%27%3D%271
X-Hurl-Case: xpath-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--query-after
HTTP 200


# xpath-01--query-double | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2527%2520or%2520%25271%2527%253D%25271
X-Hurl-Case: xpath-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--query-double-after
HTTP 200


# xpath-01--query-duplicate | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%27%20or%20%271%27%3D%271
X-Hurl-Case: xpath-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--query-duplicate-after
HTTP 200


# xpath-01--header | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xpath-01--header
X-Probe: %27%20or%20%271%27%3D%271
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--header-after
HTTP 200


# xpath-01--cookie | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xpath-01--cookie
Cookie: probe=%27%20or%20%271%27%3D%271
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--cookie-after
HTTP 200


# xpath-01--form | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%27%20or%20%271%27%3D%271
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--form-after
HTTP 200


# xpath-01--json | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--json
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--json-after
HTTP 200


# xpath-01--json-put | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: xpath-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--json-put-after
HTTP 200


# xpath-01--json-patch | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: xpath-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--json-patch-after
HTTP 200


# xpath-01--text | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--text
Content-Type: text/plain
```
' or '1'='1
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--text-after
HTTP 200


# xpath-01--xml | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--xml
Content-Type: application/xml
```
<root><value>' or '1'='1</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--xml-after
HTTP 200


# xpath-01--multipart | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJyBvciAnMSc9JzENCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--multipart-after
HTTP 200


# xpath-01--gzip | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUlfIL1JQN1S3VTdUiq2tBQDY6chQLAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--gzip-after
HTTP 200


# xpath-01--deflate | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVJXyC9SUDdUt1U3VIqtrQUAP/ENaA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--deflate-after
HTTP 200


# xpath-01--br | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixWAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCInIG9yICcxJz0nMSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-01--br-after
HTTP 200


# xpath-bypass-01--query | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=count(%2F%2Fuser)%3E0
X-Hurl-Case: xpath-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-after
HTTP 200


# xpath-bypass-01--query-double | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=count(%252F%252Fuser)%253E0
X-Hurl-Case: xpath-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-double-after
HTTP 200


# xpath-bypass-01--query-duplicate | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=count(%2F%2Fuser)%3E0
X-Hurl-Case: xpath-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--query-duplicate-after
HTTP 200


# xpath-bypass-01--header | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--header
X-Probe: count(%2F%2Fuser)%3E0
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--header-after
HTTP 200


# xpath-bypass-01--cookie | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--cookie
Cookie: probe=count(%2F%2Fuser)%3E0
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--cookie-after
HTTP 200


# xpath-bypass-01--form | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=count(%2F%2Fuser)%3E0
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--form-after
HTTP 200


# xpath-bypass-01--json | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","count(//user)>0"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-after
HTTP 200


# xpath-bypass-01--json-put | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","count(//user)>0"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-put-after
HTTP 200


# xpath-bypass-01--json-patch | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","count(//user)>0"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--json-patch-after
HTTP 200


# xpath-bypass-01--text | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--text
Content-Type: text/plain
```
count(//user)>0
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--text-after
HTTP 200


# xpath-bypass-01--xml | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--xml
Content-Type: application/xml
```
<root><value>count(//user)&gt;0</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--xml-after
HTTP 200


# xpath-bypass-01--multipart | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KY291bnQoLy91c2VyKT4wDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--multipart-after
HTTP 200


# xpath-bypass-01--gzip | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSs4vzSvR0NcvLU4t0rQzUIqtrQUAPiO31zAAAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--gzip-after
HTTP 200


# xpath-bypass-01--deflate | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrOL80r0dDXLy1OLdK0M1CKra0FAI1cEBE=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--deflate-after
HTTP 200


# xpath-bypass-01--br | XPath注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: xpath-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,Gy8AiCwO7JjERIrBTBPp1yIvW3vLp6tYdAqmL0VQUmADTsDD1oBuTMjUwiEvyJHDhlX14w==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: xpath-bypass-01--br-after
HTTP 200


# extra-xpath-boolean--query | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-before
HTTP 200

GET {{base_url}}/probe?q=%27%20or%20%271%27%3D%271
X-Hurl-Case: extra-xpath-boolean--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-after
HTTP 200


# extra-xpath-boolean--query-double | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%2527%2520or%2520%25271%2527%253D%25271
X-Hurl-Case: extra-xpath-boolean--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-double-after
HTTP 200


# extra-xpath-boolean--query-duplicate | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%27%20or%20%271%27%3D%271
X-Hurl-Case: extra-xpath-boolean--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--query-duplicate-after
HTTP 200


# extra-xpath-boolean--header | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--header
X-Probe: %27%20or%20%271%27%3D%271
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--header-after
HTTP 200


# extra-xpath-boolean--cookie | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--cookie
Cookie: probe=%27%20or%20%271%27%3D%271
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--cookie-after
HTTP 200


# extra-xpath-boolean--form | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--form
Content-Type: application/x-www-form-urlencoded
```
q=%27%20or%20%271%27%3D%271
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--form-after
HTTP 200


# extra-xpath-boolean--json | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--json
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-after
HTTP 200


# extra-xpath-boolean--json-put | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--json-put
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-put-after
HTTP 200


# extra-xpath-boolean--json-patch | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","' or '1'='1"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--json-patch-after
HTTP 200


# extra-xpath-boolean--text | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--text
Content-Type: text/plain
```
' or '1'='1
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--text-after
HTTP 200


# extra-xpath-boolean--xml | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--xml
Content-Type: application/xml
```
<root><value>' or '1'='1</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--xml-after
HTTP 200


# extra-xpath-boolean--multipart | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KJyBvciAnMSc9JzENCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--multipart-after
HTTP 200


# extra-xpath-boolean--gzip | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRUlfIL1JQN1S3VTdUiq2tBQDY6chQLAAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--gzip-after
HTTP 200


# extra-xpath-boolean--deflate | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUVJXyC9SUDdUt1U3VIqtrQUAP/ENaA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--deflate-after
HTTP 200


# extra-xpath-boolean--br | xpath | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-xpath-boolean--br
Content-Type: application/json
Content-Encoding: br
base64,ixWAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCInIG9yICcxJz0nMSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-xpath-boolean--br-after
HTTP 200


