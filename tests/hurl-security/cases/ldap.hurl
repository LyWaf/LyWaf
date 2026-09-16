# ldap regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# ldap-01--query | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--query-before
HTTP 200

GET {{base_url}}/probe?q=*)(%7C(mail%3D*))
X-Hurl-Case: ldap-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--query-after
HTTP 200


# ldap-01--query-double | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=*)(%257C(mail%253D*))
X-Hurl-Case: ldap-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--query-double-after
HTTP 200


# ldap-01--query-duplicate | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=*)(%7C(mail%3D*))
X-Hurl-Case: ldap-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--query-duplicate-after
HTTP 200


# ldap-01--header | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ldap-01--header
X-Probe: *)(%7C(mail%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--header-after
HTTP 200


# ldap-01--cookie | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ldap-01--cookie
Cookie: probe=*)(%7C(mail%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--cookie-after
HTTP 200


# ldap-01--form | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--form
Content-Type: application/x-www-form-urlencoded
```
q=*)(%7C(mail%3D*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--form-after
HTTP 200


# ldap-01--json | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--json
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(mail=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--json-after
HTTP 200


# ldap-01--json-put | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: ldap-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(mail=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--json-put-after
HTTP 200


# ldap-01--json-patch | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: ldap-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(mail=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--json-patch-after
HTTP 200


# ldap-01--text | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--text
Content-Type: text/plain
```
*)(|(mail=*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--text-after
HTTP 200


# ldap-01--xml | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--xml
Content-Type: application/xml
```
<root><value>*)(|(mail=*))</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--xml-after
HTTP 200


# ldap-01--multipart | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KKikofChtYWlsPSopKQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--multipart-after
HTTP 200


# ldap-01--gzip | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0tLUqNHITczMsdXS1FSKra0FABzoyGguAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--gzip-after
HTTP 200


# ldap-01--deflate | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdLS1KjRyE3MzLHV0tRUiq2tBQBhMg6H;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--deflate-after
HTTP 200


# ldap-01--br | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixaAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIqKSh8KG1haWw9KikpIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-01--br-after
HTTP 200


# ldap-bypass-01--query | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=(uid%3D*)((%7C(role%3D*))
X-Hurl-Case: ldap-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-after
HTTP 200


# ldap-bypass-01--query-double | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=(uid%253D*)((%257C(role%253D*))
X-Hurl-Case: ldap-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-double-after
HTTP 200


# ldap-bypass-01--query-duplicate | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=(uid%3D*)((%7C(role%3D*))
X-Hurl-Case: ldap-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--query-duplicate-after
HTTP 200


# ldap-bypass-01--header | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--header
X-Probe: (uid%3D*)((%7C(role%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--header-after
HTTP 200


# ldap-bypass-01--cookie | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--cookie
Cookie: probe=(uid%3D*)((%7C(role%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--cookie-after
HTTP 200


# ldap-bypass-01--form | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=(uid%3D*)((%7C(role%3D*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--form-after
HTTP 200


# ldap-bypass-01--json | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","(uid=*)((|(role=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-after
HTTP 200


# ldap-bypass-01--json-put | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","(uid=*)((|(role=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-put-after
HTTP 200


# ldap-bypass-01--json-patch | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","(uid=*)((|(role=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--json-patch-after
HTTP 200


# ldap-bypass-01--text | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--text
Content-Type: text/plain
```
(uid=*)((|(role=*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--text-after
HTTP 200


# ldap-bypass-01--xml | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--xml
Content-Type: application/xml
```
<root><value>(uid=*)((|(role=*))</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--xml-after
HTTP 200


# ldap-bypass-01--multipart | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KKHVpZD0qKSgofChyb2xlPSopKQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--multipart-after
HTTP 200


# ldap-bypass-01--gzip | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0ijNTLHV0tTQqNEoys9JtdXS1FSKra0FAJiXi3s0AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--gzip-after
HTTP 200


# ldap-bypass-01--deflate | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdIozUyx1dLU0KjRKMrPSbXV0tRUiq2tBQDAxhBl;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--deflate-after
HTTP 200


# ldap-bypass-01--br | LDAP注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: ldap-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,ixmAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIodWlkPSopKCh8KHJvbGU9KikpIl19fQM=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: ldap-bypass-01--br-after
HTTP 200


# extra-ldap-filter--query | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-before
HTTP 200

GET {{base_url}}/probe?q=*)(%7C(uid%3D*))
X-Hurl-Case: extra-ldap-filter--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-after
HTTP 200


# extra-ldap-filter--query-double | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-double-before
HTTP 200

GET {{base_url}}/probe?q=*)(%257C(uid%253D*))
X-Hurl-Case: extra-ldap-filter--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-double-after
HTTP 200


# extra-ldap-filter--query-duplicate | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=*)(%7C(uid%3D*))
X-Hurl-Case: extra-ldap-filter--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--query-duplicate-after
HTTP 200


# extra-ldap-filter--header | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--header
X-Probe: *)(%7C(uid%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--header-after
HTTP 200


# extra-ldap-filter--cookie | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--cookie
Cookie: probe=*)(%7C(uid%3D*))
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--cookie-after
HTTP 200


# extra-ldap-filter--form | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--form
Content-Type: application/x-www-form-urlencoded
```
q=*)(%7C(uid%3D*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--form-after
HTTP 200


# extra-ldap-filter--json | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--json
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(uid=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-after
HTTP 200


# extra-ldap-filter--json-put | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--json-put
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(uid=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-put-after
HTTP 200


# extra-ldap-filter--json-patch | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","*)(|(uid=*))"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--json-patch-after
HTTP 200


# extra-ldap-filter--text | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--text
Content-Type: text/plain
```
*)(|(uid=*))
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--text-after
HTTP 200


# extra-ldap-filter--xml | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--xml
Content-Type: application/xml
```
<root><value>*)(|(uid=*))</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--xml-after
HTTP 200


# extra-ldap-filter--multipart | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KKikofCh1aWQ9KikpDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--multipart-after
HTTP 200


# extra-ldap-filter--gzip | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0tLUqNEozUyx1dLUVIqtrQUA3LnVTC0AAAA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--gzip-after
HTTP 200


# extra-ldap-filter--deflate | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdLS1KjRKM1MsdXS1FSKra0FAFH4DiY=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--deflate-after
HTTP 200


# extra-ldap-filter--br | ldap | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-ldap-filter--br
Content-Type: application/json
Content-Encoding: br
base64,CxaAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIqKSh8KHVpZD0qKSkiXX19Aw==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-ldap-filter--br-after
HTTP 200


