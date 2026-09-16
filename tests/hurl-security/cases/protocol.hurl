# protocol regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# edge-invalid-gzip--raw | protocol | Original transport/boundary probe
GET {{base_url}}/
X-Hurl-Case: edge-invalid-gzip--raw-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: edge-invalid-gzip--raw
Content-Type: application/json
Content-Encoding: gzip
base64,bm90LWd6aXA=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: edge-invalid-gzip--raw-after
HTTP 200


# edge-unknown-encoding--raw | protocol | Original transport/boundary probe
GET {{base_url}}/
X-Hurl-Case: edge-unknown-encoding--raw-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: edge-unknown-encoding--raw
Content-Type: text/plain
Content-Encoding: unsupported
base64,JyBvciAxPTEgLS0=;
HTTP 403

GET {{base_url}}/
X-Hurl-Case: edge-unknown-encoding--raw-after
HTTP 200


