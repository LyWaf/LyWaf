# nosql regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# nosql-01--query | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22%24ne%22%3Anull%7D
X-Hurl-Case: nosql-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--query-after
HTTP 200


# nosql-01--query-double | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522%2524ne%2522%253Anull%257D
X-Hurl-Case: nosql-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--query-double-after
HTTP 200


# nosql-01--query-duplicate | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22%24ne%22%3Anull%7D
X-Hurl-Case: nosql-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--query-duplicate-after
HTTP 200


# nosql-01--header | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: nosql-01--header
X-Probe: %7B%22%24ne%22%3Anull%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--header-after
HTTP 200


# nosql-01--cookie | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: nosql-01--cookie
Cookie: probe=%7B%22%24ne%22%3Anull%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--cookie-after
HTTP 200


# nosql-01--form | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22%24ne%22%3Anull%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--form-after
HTTP 200


# nosql-01--json | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--json-after
HTTP 200


# nosql-01--json-put | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: nosql-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--json-put-after
HTTP 200


# nosql-01--json-patch | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: nosql-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--json-patch-after
HTTP 200


# nosql-01--text | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--text
Content-Type: text/plain
```
{"$ne":null}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--text-after
HTTP 200


# nosql-01--xml | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--xml
Content-Type: application/xml
```
<root><value>{"$ne":null}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--xml-after
HTTP 200


# nosql-01--multipart | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyIkbmUiOm51bGx9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--multipart-after
HTTP 200


# nosql-01--gzip | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqo5RUslLjVGyyivNyalViq2tBQDYn6qhLwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--gzip-after
HTTP 200


# nosql-01--deflate | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaqOUVLJS41RssorzcmpVYqtrQUAeeUP7A==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--deflate-after
HTTP 200


# nosql-01--br | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-01--br
Content-Type: application/json
Content-Encoding: br
base64,CxeAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7XCIkbmVcIjpudWxsfSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-01--br-after
HTTP 200


# nosql-bypass-01--query | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22%24where%22%3A%22this.role%20%3D%3D%20%27admin%27%22%7D
X-Hurl-Case: nosql-bypass-01--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-after
HTTP 200


# nosql-bypass-01--query-double | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522%2524where%2522%253A%2522this.role%2520%253D%253D%2520%2527admin%2527%2522%257D
X-Hurl-Case: nosql-bypass-01--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-double-after
HTTP 200


# nosql-bypass-01--query-duplicate | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22%24where%22%3A%22this.role%20%3D%3D%20%27admin%27%22%7D
X-Hurl-Case: nosql-bypass-01--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--query-duplicate-after
HTTP 200


# nosql-bypass-01--header | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--header
X-Probe: %7B%22%24where%22%3A%22this.role%20%3D%3D%20%27admin%27%22%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--header-after
HTTP 200


# nosql-bypass-01--cookie | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--cookie
Cookie: probe=%7B%22%24where%22%3A%22this.role%20%3D%3D%20%27admin%27%22%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--cookie-after
HTTP 200


# nosql-bypass-01--form | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22%24where%22%3A%22this.role%20%3D%3D%20%27admin%27%22%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--form-after
HTTP 200


# nosql-bypass-01--json | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"$where\":\"this.role == 'admin'\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-after
HTTP 200


# nosql-bypass-01--json-put | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"$where\":\"this.role == 'admin'\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-put-after
HTTP 200


# nosql-bypass-01--json-patch | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"$where\":\"this.role == 'admin'\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--json-patch-after
HTTP 200


# nosql-bypass-01--text | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--text
Content-Type: text/plain
```
{"$where":"this.role == 'admin'"}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--text-after
HTTP 200


# nosql-bypass-01--xml | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--xml
Content-Type: application/xml
```
<root><value>{"$where":"this.role == 'admin'"}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--xml-after
HTTP 200


# nosql-bypass-01--multipart | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyIkd2hlcmUiOiJ0aGlzLnJvbGUgPT0gJ2FkbWluJyJ9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--multipart-after
HTTP 200


# nosql-bypass-01--gzip | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqo5RUinPSC1KjVGyilEqycgs1ivKz0lVsLVVUE9Myc3MU49RqlWKra0FAGxNvnlGAAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--gzip-after
HTTP 200


# nosql-bypass-01--deflate | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaqOUVIpz0gtSo1RsopRKsnILNYrys9JVbC1VVBPTMnNzFOPUapViq2tBQA5sxce;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--deflate-after
HTTP 200


# nosql-bypass-01--br | NoSQL注入 | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: nosql-bypass-01--br
Content-Type: application/json
Content-Encoding: br
base64,G0UAAIzTJfYzKVnQmkv1OQZfFpKpzqUkRo8HXPQrLZYieYydIYoiCiAomAQ91M8l5uSckRkU3s+zhXCZlt8C;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: nosql-bypass-01--br-after
HTTP 200


# extra-nosql-ne--query | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22%24ne%22%3Anull%7D
X-Hurl-Case: extra-nosql-ne--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-after
HTTP 200


# extra-nosql-ne--query-double | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522%2524ne%2522%253Anull%257D
X-Hurl-Case: extra-nosql-ne--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-double-after
HTTP 200


# extra-nosql-ne--query-duplicate | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22%24ne%22%3Anull%7D
X-Hurl-Case: extra-nosql-ne--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--query-duplicate-after
HTTP 200


# extra-nosql-ne--header | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--header
X-Probe: %7B%22%24ne%22%3Anull%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--header-after
HTTP 200


# extra-nosql-ne--cookie | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--cookie
Cookie: probe=%7B%22%24ne%22%3Anull%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--cookie-after
HTTP 200


# extra-nosql-ne--form | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22%24ne%22%3Anull%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--form-after
HTTP 200


# extra-nosql-ne--json | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-after
HTTP 200


# extra-nosql-ne--json-put | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-put-after
HTTP 200


# extra-nosql-ne--json-patch | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"$ne\":null}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--json-patch-after
HTTP 200


# extra-nosql-ne--text | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--text
Content-Type: text/plain
```
{"$ne":null}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--text-after
HTTP 200


# extra-nosql-ne--xml | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--xml
Content-Type: application/xml
```
<root><value>{"$ne":null}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--xml-after
HTTP 200


# extra-nosql-ne--multipart | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyIkbmUiOm51bGx9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--multipart-after
HTTP 200


# extra-nosql-ne--gzip | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqo5RUslLjVGyyivNyalViq2tBQDYn6qhLwAAAA==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--gzip-after
HTTP 200


# extra-nosql-ne--deflate | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaqOUVLJS41RssorzcmpVYqtrQUAeeUP7A==;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--deflate-after
HTTP 200


# extra-nosql-ne--br | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-ne--br
Content-Type: application/json
Content-Encoding: br
base64,CxeAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJ7XCIkbmVcIjpudWxsfSJdfX0D;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-ne--br-after
HTTP 200


# extra-nosql-regex--query | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22%24regex%22%3A%22.*%22%7D
X-Hurl-Case: extra-nosql-regex--query
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-after
HTTP 200


# extra-nosql-regex--query-double | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522%2524regex%2522%253A%2522.*%2522%257D
X-Hurl-Case: extra-nosql-regex--query-double
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-double-after
HTTP 200


# extra-nosql-regex--query-duplicate | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22%24regex%22%3A%22.*%22%7D
X-Hurl-Case: extra-nosql-regex--query-duplicate
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--query-duplicate-after
HTTP 200


# extra-nosql-regex--header | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--header
X-Probe: %7B%22%24regex%22%3A%22.*%22%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--header-after
HTTP 200


# extra-nosql-regex--cookie | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--cookie
Cookie: probe=%7B%22%24regex%22%3A%22.*%22%7D
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--cookie-after
HTTP 200


# extra-nosql-regex--form | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22%24regex%22%3A%22.*%22%7D
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--form-after
HTTP 200


# extra-nosql-regex--json | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"$regex\":\".*\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-after
HTTP 200


# extra-nosql-regex--json-put | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"$regex\":\".*\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-put-after
HTTP 200


# extra-nosql-regex--json-patch | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"$regex\":\".*\"}"]}}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--json-patch-after
HTTP 200


# extra-nosql-regex--text | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--text
Content-Type: text/plain
```
{"$regex":".*"}
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--text-after
HTTP 200


# extra-nosql-regex--xml | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--xml
Content-Type: application/xml
```
<root><value>{"$regex":".*"}</value></root>
```
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--xml-after
HTTP 200


# extra-nosql-regex--multipart | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyIkcmVnZXgiOiIuKiJ9DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--multipart-after
HTTP 200


# extra-nosql-regex--gzip | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRqo5RUilKTU+tiFGyilHS04pRqlWKra0FAHr4h7U0AAAA;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--gzip-after
HTTP 200


# extra-nosql-regex--deflate | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUaqOUVIpSk1PrYhRsopR0tOKUapViq2tBQDH4xDN;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--deflate-after
HTTP 200


# extra-nosql-regex--br | nosql | Original synthetic probe
GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: extra-nosql-regex--br
Content-Type: application/json
Content-Encoding: br
base64,GzMA+AXKbakfSyLSP1XfOOwmYawlIUUViDQAGOQABwpFxaJNh0mZeHnzG5k1qDnd;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: extra-nosql-regex--br-after
HTTP 200


