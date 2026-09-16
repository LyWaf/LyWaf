# sensitive-path regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# path-1410--path | sensitive-path | Original path probe
GET {{base_url}}/
X-Hurl-Case: path-1410--path-before
HTTP 200

GET {{base_url}}/.git/config
X-Hurl-Case: path-1410--path
HTTP 403

GET {{base_url}}/
X-Hurl-Case: path-1410--path-after
HTTP 200


# path-1411--path | sensitive-path | Original path probe
GET {{base_url}}/
X-Hurl-Case: path-1411--path-before
HTTP 200

GET {{base_url}}/.env
X-Hurl-Case: path-1411--path
HTTP 403

GET {{base_url}}/
X-Hurl-Case: path-1411--path-after
HTTP 200


# path-1412--path | sensitive-path | Original path probe
GET {{base_url}}/
X-Hurl-Case: path-1412--path-before
HTTP 200

GET {{base_url}}/../../etc/passwd
X-Hurl-Case: path-1412--path
HTTP 403

GET {{base_url}}/
X-Hurl-Case: path-1412--path-after
HTTP 200


# path-1413--path | sensitive-path | Original path probe
GET {{base_url}}/
X-Hurl-Case: path-1413--path-before
HTTP 200

GET {{base_url}}/%2e%2e/%2e%2e/etc/passwd
X-Hurl-Case: path-1413--path
HTTP 403

GET {{base_url}}/
X-Hurl-Case: path-1413--path-after
HTTP 200


# path-1414--path | sensitive-path | Original path probe
GET {{base_url}}/
X-Hurl-Case: path-1414--path-before
HTTP 200

GET {{base_url}}/wp-config.php.bak
X-Hurl-Case: path-1414--path
HTTP 403

GET {{base_url}}/
X-Hurl-Case: path-1414--path-after
HTTP 200


