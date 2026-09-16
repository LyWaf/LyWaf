# Run only against the isolated fixture started by run.mjs.
GET {{base_url}}/control
HTTP 200
[Asserts]
header "X-Hurl-Origin" == "local-fixture"
body == "origin-ok"

# SQL injection in a URL parameter.
GET {{base_url}}/probe?q=%27%20or%201%3D1%20--
HTTP 403
[Asserts]
header "X-Hurl-Origin" not exists

# SQL injection in a JSON body (Base64 is Hurl byte syntax, not a wire encoding).
POST {{base_url}}/probe
Content-Type: application/json
base64,eyJxIjoiJyBvciAxPTEgLS0ifQ==;
HTTP 403
[Asserts]
header "X-Hurl-Origin" not exists

GET {{base_url}}/control
HTTP 200
[Asserts]
header "X-Hurl-Origin" == "local-fixture"
body == "origin-ok"
