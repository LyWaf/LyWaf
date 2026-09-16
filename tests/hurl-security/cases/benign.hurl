# benign regression cases
# Set base_url to an authorized test WAF (no trailing slash).
# The upstream root must return 200 for before/after controls.
# Use --continue-on-error --path-as-is --jobs 1.

# benign-01--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--query-before
HTTP 200

GET {{base_url}}/probe?q=select%20a%20delivery%20method
X-Hurl-Case: benign-01--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--query-after
HTTP 200


# benign-01--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--query-double-before
HTTP 200

GET {{base_url}}/probe?q=select%2520a%2520delivery%2520method
X-Hurl-Case: benign-01--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--query-double-after
HTTP 200


# benign-01--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=select%20a%20delivery%20method
X-Hurl-Case: benign-01--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--query-duplicate-after
HTTP 200


# benign-01--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-01--header
X-Probe: select%20a%20delivery%20method
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--header-after
HTTP 200


# benign-01--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-01--cookie
Cookie: probe=select%20a%20delivery%20method
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--cookie-after
HTTP 200


# benign-01--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--form
Content-Type: application/x-www-form-urlencoded
```
q=select%20a%20delivery%20method
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--form-after
HTTP 200


# benign-01--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--json
Content-Type: application/json
```
{"data":{"values":["normal","select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--json-after
HTTP 200


# benign-01--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-01--json-put
Content-Type: application/json
```
{"data":{"values":["normal","select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--json-put-after
HTTP 200


# benign-01--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-01--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--json-patch-after
HTTP 200


# benign-01--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--text
Content-Type: text/plain
```
select a delivery method
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--text-after
HTTP 200


# benign-01--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--xml
Content-Type: application/xml
```
<root><value>select a delivery method</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--xml-after
HTTP 200


# benign-01--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0Kc2VsZWN0IGEgZGVsaXZlcnkgbWV0aG9kDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--multipart-after
HTTP 200


# benign-01--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRKk7NSU0uUUhUSEnNySxLLapUyE0tychPUYqtrQUAKVLIHzkAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--gzip-after
HTTP 200


# benign-01--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUSpOzUlNLlFIVEhJzcksSy2qVMhNLcnIT1GKra0FADloFDI=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--deflate-after
HTTP 200


# benign-01--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-01--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-01--br
Content-Type: application/json
Content-Encoding: br
base64,GzgAiBwHbqzFThB802uRvbCtqcPSFX3gkAOXc5An7bABJ5omr+MNJAEZOdkmPxL6Nw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-01--br-after
HTTP 200


# benign-02--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--query-before
HTTP 200

GET {{base_url}}/probe?q=Our%20labor%20union%20selected%20a%20chairperson
X-Hurl-Case: benign-02--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--query-after
HTTP 200


# benign-02--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Our%2520labor%2520union%2520selected%2520a%2520chairperson
X-Hurl-Case: benign-02--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--query-double-after
HTTP 200


# benign-02--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Our%20labor%20union%20selected%20a%20chairperson
X-Hurl-Case: benign-02--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--query-duplicate-after
HTTP 200


# benign-02--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-02--header
X-Probe: Our%20labor%20union%20selected%20a%20chairperson
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--header-after
HTTP 200


# benign-02--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-02--cookie
Cookie: probe=Our%20labor%20union%20selected%20a%20chairperson
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--cookie-after
HTTP 200


# benign-02--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--form
Content-Type: application/x-www-form-urlencoded
```
q=Our%20labor%20union%20selected%20a%20chairperson
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--form-after
HTTP 200


# benign-02--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--json
Content-Type: application/json
```
{"data":{"values":["normal","Our labor union selected a chairperson"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--json-after
HTTP 200


# benign-02--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-02--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Our labor union selected a chairperson"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--json-put-after
HTTP 200


# benign-02--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-02--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Our labor union selected a chairperson"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--json-patch-after
HTTP 200


# benign-02--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--text
Content-Type: text/plain
```
Our labor union selected a chairperson
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--text-after
HTTP 200


# benign-02--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--xml
Content-Type: application/xml
```
<root><value>Our labor union selected a chairperson</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--xml-after
HTTP 200


# benign-02--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KT3VyIGxhYm9yIHVuaW9uIHNlbGVjdGVkIGEgY2hhaXJwZXJzb24NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--multipart-after
HTTP 200


# benign-02--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBwQ2AIAwF0FWaf3YClnAA46FCE0lqawp4IezuexOFOyNNfKxDGtIB83hYsWEfQcqXBw2rbtREJXcpxJRvrvFKNDeca/2RCjJSRwAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--gzip-after
HTTP 200


# benign-02--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwcENgCAMBdBVmn92ApZwAOOhQhNJamsKeCHs7nsThTsjTXysQxrSAfN4WLFhH0HKlwcNq27URCV3KcSUb67xSjQ3nGv9dXIZYw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--deflate-after
HTTP 200


# benign-02--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-02--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-02--br
Content-Type: application/json
Content-Encoding: br
base64,G0YAEBwHdiyzjSA87W2EWmRv62nqsHRlX50cOOTA4W15gC2wPOBmrHNOilIhgRFxHD3Z7Y4k34uUlqMG;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-02--br-after
HTTP 200


# benign-03--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--query-before
HTTP 200

GET {{base_url}}/probe?q=https%3A%2F%2Fexample.com%2Foauth%2Fcallback
X-Hurl-Case: benign-03--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--query-after
HTTP 200


# benign-03--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--query-double-before
HTTP 200

GET {{base_url}}/probe?q=https%253A%252F%252Fexample.com%252Foauth%252Fcallback
X-Hurl-Case: benign-03--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--query-double-after
HTTP 200


# benign-03--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=https%3A%2F%2Fexample.com%2Foauth%2Fcallback
X-Hurl-Case: benign-03--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--query-duplicate-after
HTTP 200


# benign-03--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-03--header
X-Probe: https%3A%2F%2Fexample.com%2Foauth%2Fcallback
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--header-after
HTTP 200


# benign-03--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-03--cookie
Cookie: probe=https%3A%2F%2Fexample.com%2Foauth%2Fcallback
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--cookie-after
HTTP 200


# benign-03--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--form
Content-Type: application/x-www-form-urlencoded
```
q=https%3A%2F%2Fexample.com%2Foauth%2Fcallback
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--form-after
HTTP 200


# benign-03--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--json
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/oauth/callback"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--json-after
HTTP 200


# benign-03--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-03--json-put
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/oauth/callback"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--json-put-after
HTTP 200


# benign-03--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-03--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/oauth/callback"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--json-patch-after
HTTP 200


# benign-03--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--text
Content-Type: text/plain
```
https://example.com/oauth/callback
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--text-after
HTTP 200


# benign-03--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--xml
Content-Type: application/xml
```
<root><value>https://example.com/oauth/callback</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--xml-after
HTTP 200


# benign-03--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cHM6Ly9leGFtcGxlLmNvbS9vYXV0aC9jYWxsYmFjaw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--multipart-after
HTTP 200


# benign-03--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBQQqAIBAF0Lv8dTR7rxItfiYIzaTkGIF4994bOOlEGHipPTWEDXd5jIoF2b22IJI+WtW0xmJS2D1LpOrBeGGf8wddiPw/QwAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--gzip-after
HTTP 200


# benign-03--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwUEKgCAQBdC7/HU0e68SLX4mCM2k5BiBePfeGzjpRBh4qT01hA13eYyKBdm9tiCSPlrVtMZiUtg9S6TqwXhhn/MHFhIX3A==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--deflate-after
HTTP 200


# benign-03--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-03--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-03--br
Content-Type: application/json
Content-Encoding: br
base64,G0IAECwO7CbPz8OamjJ70fFLka297HsLSx2WrhShq0uRFzRwyIFDCyTAduDIgw04kXQbhkFGPgPMhMiTq6kXqD13l60B;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-03--br-after
HTTP 200


# benign-04--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22status%22%3A%22active%22%2C%22filter%22%3A%22recent%22%7D
X-Hurl-Case: benign-04--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--query-after
HTTP 200


# benign-04--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522status%2522%253A%2522active%2522%252C%2522filter%2522%253A%2522recent%2522%257D
X-Hurl-Case: benign-04--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--query-double-after
HTTP 200


# benign-04--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22status%22%3A%22active%22%2C%22filter%22%3A%22recent%22%7D
X-Hurl-Case: benign-04--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--query-duplicate-after
HTTP 200


# benign-04--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-04--header
X-Probe: %7B%22status%22%3A%22active%22%2C%22filter%22%3A%22recent%22%7D
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--header-after
HTTP 200


# benign-04--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-04--cookie
Cookie: probe=%7B%22status%22%3A%22active%22%2C%22filter%22%3A%22recent%22%7D
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--cookie-after
HTTP 200


# benign-04--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22status%22%3A%22active%22%2C%22filter%22%3A%22recent%22%7D
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--form-after
HTTP 200


# benign-04--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"status\":\"active\",\"filter\":\"recent\"}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--json-after
HTTP 200


# benign-04--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-04--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"status\":\"active\",\"filter\":\"recent\"}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--json-put-after
HTTP 200


# benign-04--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-04--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"status\":\"active\",\"filter\":\"recent\"}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--json-patch-after
HTTP 200


# benign-04--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--text
Content-Type: text/plain
```
{"status":"active","filter":"recent"}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--text-after
HTTP 200


# benign-04--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--xml
Content-Type: application/xml
```
<root><value>{"status":"active","filter":"recent"}</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--xml-after
HTTP 200


# benign-04--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyJzdGF0dXMiOiJhY3RpdmUiLCJmaWx0ZXIiOiJyZWNlbnQifQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--multipart-after
HTTP 200


# benign-04--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACh3IwQnAIAxA0V3+2Qlcpekh2BQEa0GjF3H3Qt/xLS51JS6mlmGdeFDf9mghsITu6qMLUdDkeZoQhDsXt/Zvs2TVhc259wdIFcamTgAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--gzip-after
HTTP 200


# benign-04--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwdyMEJwCAMQNFd/tkJXKXpIdgUBGtBoxdx90Lf8S0udSUuppZhnXhQ3/ZoIbCE7uqjC1HQ5HmaEIQ7F7f2b7Nk1YXNufcHFx8auw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--deflate-after
HTTP 200


# benign-04--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-04--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-04--br
Content-Type: application/json
Content-Encoding: br
base64,G00AqBSh0gnNI38uUEiXEfUKDjlgDTjgUztWAUyaRrCuyPLI087YClaKWVPCSt3Fzr8D;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-04--br-after
HTTP 200


# benign-05--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--query-before
HTTP 200

GET {{base_url}}/probe?q=%2Fdocs%2Fsql%2Fselect
X-Hurl-Case: benign-05--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--query-after
HTTP 200


# benign-05--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%252Fdocs%252Fsql%252Fselect
X-Hurl-Case: benign-05--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--query-double-after
HTTP 200


# benign-05--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%2Fdocs%2Fsql%2Fselect
X-Hurl-Case: benign-05--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--query-duplicate-after
HTTP 200


# benign-05--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-05--header
X-Probe: %2Fdocs%2Fsql%2Fselect
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--header-after
HTTP 200


# benign-05--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-05--cookie
Cookie: probe=%2Fdocs%2Fsql%2Fselect
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--cookie-after
HTTP 200


# benign-05--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--form
Content-Type: application/x-www-form-urlencoded
```
q=%2Fdocs%2Fsql%2Fselect
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--form-after
HTTP 200


# benign-05--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--json
Content-Type: application/json
```
{"data":{"values":["normal","/docs/sql/select"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--json-after
HTTP 200


# benign-05--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-05--json-put
Content-Type: application/json
```
{"data":{"values":["normal","/docs/sql/select"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--json-put-after
HTTP 200


# benign-05--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-05--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","/docs/sql/select"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--json-patch-after
HTTP 200


# benign-05--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--text
Content-Type: text/plain
```
/docs/sql/select
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--text-after
HTTP 200


# benign-05--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--xml
Content-Type: application/xml
```
<root><value>/docs/sql/select</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--xml-after
HTTP 200


# benign-05--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KL2RvY3Mvc3FsL3NlbGVjdA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--multipart-after
HTTP 200


# benign-05--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0k/JTy7WLy7M0S9OzUlNLlGKra0FAItjOLAxAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--gzip-after
HTTP 200


# benign-05--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdJPyU8u1i8uzNEvTs1JTS5Riq2tBQCgtRES;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--deflate-after
HTTP 200


# benign-05--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-05--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-05--br
Content-Type: application/json
Content-Encoding: br
base64,CxiAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIvZG9jcy9zcWwvc2VsZWN0Il19fQM=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-05--br-after
HTTP 200


# benign-06--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--query-before
HTTP 200

GET {{base_url}}/probe?q=Mozilla%2F5.0%20Chrome%2F140%20Safari%2F537.36
X-Hurl-Case: benign-06--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--query-after
HTTP 200


# benign-06--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Mozilla%252F5.0%2520Chrome%252F140%2520Safari%252F537.36
X-Hurl-Case: benign-06--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--query-double-after
HTTP 200


# benign-06--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Mozilla%2F5.0%20Chrome%2F140%20Safari%2F537.36
X-Hurl-Case: benign-06--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--query-duplicate-after
HTTP 200


# benign-06--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-06--header
X-Probe: Mozilla%2F5.0%20Chrome%2F140%20Safari%2F537.36
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--header-after
HTTP 200


# benign-06--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-06--cookie
Cookie: probe=Mozilla%2F5.0%20Chrome%2F140%20Safari%2F537.36
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--cookie-after
HTTP 200


# benign-06--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--form
Content-Type: application/x-www-form-urlencoded
```
q=Mozilla%2F5.0%20Chrome%2F140%20Safari%2F537.36
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--form-after
HTTP 200


# benign-06--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--json
Content-Type: application/json
```
{"data":{"values":["normal","Mozilla/5.0 Chrome/140 Safari/537.36"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--json-after
HTTP 200


# benign-06--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-06--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Mozilla/5.0 Chrome/140 Safari/537.36"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--json-put-after
HTTP 200


# benign-06--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-06--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Mozilla/5.0 Chrome/140 Safari/537.36"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--json-patch-after
HTTP 200


# benign-06--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--text
Content-Type: text/plain
```
Mozilla/5.0 Chrome/140 Safari/537.36
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--text-after
HTTP 200


# benign-06--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--xml
Content-Type: application/xml
```
<root><value>Mozilla/5.0 Chrome/140 Safari/537.36</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--xml-after
HTTP 200


# benign-06--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KTW96aWxsYS81LjAgQ2hyb21lLzE0MCBTYWZhcmkvNTM3LjM2DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--multipart-after
HTTP 200


# benign-06--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR8s2vyszJSdQ31TNQcM4oys9N1Tc0MVAITkxLLMrUNzU21zM2U4qtrQUAiIiPYkUAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--gzip-after
HTTP 200


# benign-06--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUfLNr8rMyUnUN9UzUHDOKMrPTdU3NDFQCE5MSyzK1Dc1NtczNlOKra0FABY0FcM=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--deflate-after
HTTP 200


# benign-06--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-06--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-06--br
Content-Type: application/json
Content-Encoding: br
base64,G0QAUBSh0iHylvpl+2cP25cqmqDYVNWAQ07AT0n2eVwGYHR0CUvaHR4RfyjxAZRAc1KLxEU4mrLpa697/tbEKvN/;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-06--br-after
HTTP 200


# benign-07--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--query-before
HTTP 200

GET {{base_url}}/probe?q=Use%20curl%20for%20local%20development
X-Hurl-Case: benign-07--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--query-after
HTTP 200


# benign-07--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Use%2520curl%2520for%2520local%2520development
X-Hurl-Case: benign-07--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--query-double-after
HTTP 200


# benign-07--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Use%20curl%20for%20local%20development
X-Hurl-Case: benign-07--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--query-duplicate-after
HTTP 200


# benign-07--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-07--header
X-Probe: Use%20curl%20for%20local%20development
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--header-after
HTTP 200


# benign-07--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-07--cookie
Cookie: probe=Use%20curl%20for%20local%20development
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--cookie-after
HTTP 200


# benign-07--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--form
Content-Type: application/x-www-form-urlencoded
```
q=Use%20curl%20for%20local%20development
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--form-after
HTTP 200


# benign-07--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--json
Content-Type: application/json
```
{"data":{"values":["normal","Use curl for local development"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--json-after
HTTP 200


# benign-07--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-07--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Use curl for local development"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--json-put-after
HTTP 200


# benign-07--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-07--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Use curl for local development"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--json-patch-after
HTTP 200


# benign-07--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--text
Content-Type: text/plain
```
Use curl for local development
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--text-after
HTTP 200


# benign-07--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--xml
Content-Type: application/xml
```
<root><value>Use curl for local development</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--xml-after
HTTP 200


# benign-07--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KVXNlIGN1cmwgZm9yIGxvY2FsIGRldmVsb3BtZW50DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--multipart-after
HTTP 200


# benign-07--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBuw3AIAwFwFWsV2cC9kgVpbDAqR4Y8WsQu+duI+lQhI2lnNYRHhRvWYkLdzeJs1E+b0KPSkm2jF6zlYH3nB9gs2cvPwAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--gzip-after
HTTP 200


# benign-07--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwbsNwCAMBcBVrFdnAvZIFaWwwKkeGPFrELvnbiPpUISNpZzWER4Ub1mJC3c3ibNRPm9Cj0pJtoxes5WB95wftFwWZA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--deflate-after
HTTP 200


# benign-07--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-07--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-07--br
Content-Type: application/json
Content-Encoding: br
base64,Gz4AiCwKbBtZfjcWBHP9Nb+wFtlj25p9Y1Wpw9KVZXN1CSIiBw45cDhYgO0EsQcbcKJp8DycwKQgI58U/2fkKZuoAQ==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-07--br-after
HTTP 200


# benign-08--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--query-before
HTTP 200

GET {{base_url}}/probe?q=file%20name%3A%20quarterly-report.pdf
X-Hurl-Case: benign-08--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--query-after
HTTP 200


# benign-08--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--query-double-before
HTTP 200

GET {{base_url}}/probe?q=file%2520name%253A%2520quarterly-report.pdf
X-Hurl-Case: benign-08--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--query-double-after
HTTP 200


# benign-08--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=file%20name%3A%20quarterly-report.pdf
X-Hurl-Case: benign-08--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--query-duplicate-after
HTTP 200


# benign-08--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-08--header
X-Probe: file%20name%3A%20quarterly-report.pdf
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--header-after
HTTP 200


# benign-08--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-08--cookie
Cookie: probe=file%20name%3A%20quarterly-report.pdf
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--cookie-after
HTTP 200


# benign-08--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--form
Content-Type: application/x-www-form-urlencoded
```
q=file%20name%3A%20quarterly-report.pdf
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--form-after
HTTP 200


# benign-08--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--json
Content-Type: application/json
```
{"data":{"values":["normal","file name: quarterly-report.pdf"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--json-after
HTTP 200


# benign-08--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-08--json-put
Content-Type: application/json
```
{"data":{"values":["normal","file name: quarterly-report.pdf"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--json-put-after
HTTP 200


# benign-08--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-08--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","file name: quarterly-report.pdf"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--json-patch-after
HTTP 200


# benign-08--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--text
Content-Type: text/plain
```
file name: quarterly-report.pdf
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--text-after
HTTP 200


# benign-08--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--xml
Content-Type: application/xml
```
<root><value>file name: quarterly-report.pdf</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--xml-after
HTTP 200


# benign-08--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KZmlsZSBuYW1lOiBxdWFydGVybHktcmVwb3J0LnBkZg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--multipart-after
HTTP 200


# benign-08--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSsvMSVXIS8xNtVIoLE0sKkktyqnULUotyC8q0StISVOKra0FAFOij9hAAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--gzip-after
HTTP 200


# benign-08--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrLzElVyEvMTbVSKCxNLCpJLcqp1C1KLcgvKtErSElTiq2tBQDQXBbh;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--deflate-after
HTTP 200


# benign-08--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-08--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-08--br
Content-Type: application/json
Content-Encoding: br
base64,Gz8AEIyTTPWdBzU58uMCedKWtShIW9EHDjlwiJOwhQcKNuBE0m1cRhnkPAA9JiiQxQiqxNofm/Oj4G81CQ==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-08--br-after
HTTP 200


# benign-09--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Croot%3E%3Citem%3Enormal%3C%2Fitem%3E%3C%2Froot%3E
X-Hurl-Case: benign-09--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--query-after
HTTP 200


# benign-09--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Croot%253E%253Citem%253Enormal%253C%252Fitem%253E%253C%252Froot%253E
X-Hurl-Case: benign-09--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--query-double-after
HTTP 200


# benign-09--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Croot%3E%3Citem%3Enormal%3C%2Fitem%3E%3C%2Froot%3E
X-Hurl-Case: benign-09--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--query-duplicate-after
HTTP 200


# benign-09--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-09--header
X-Probe: %3Croot%3E%3Citem%3Enormal%3C%2Fitem%3E%3C%2Froot%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--header-after
HTTP 200


# benign-09--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-09--cookie
Cookie: probe=%3Croot%3E%3Citem%3Enormal%3C%2Fitem%3E%3C%2Froot%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--cookie-after
HTTP 200


# benign-09--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Croot%3E%3Citem%3Enormal%3C%2Fitem%3E%3C%2Froot%3E
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--form-after
HTTP 200


# benign-09--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--json
Content-Type: application/json
```
{"data":{"values":["normal","<root><item>normal</item></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--json-after
HTTP 200


# benign-09--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-09--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<root><item>normal</item></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--json-put-after
HTTP 200


# benign-09--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-09--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<root><item>normal</item></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--json-patch-after
HTTP 200


# benign-09--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--text
Content-Type: text/plain
```
<root><item>normal</item></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--text-after
HTTP 200


# benign-09--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--xml
Content-Type: application/xml
```
<root><value>&lt;root&gt;&lt;item&gt;normal&lt;/item&gt;&lt;/root&gt;</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--xml-after
HTTP 200


# benign-09--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPHJvb3Q+PGl0ZW0+bm9ybWFsPC9pdGVtPjwvcm9vdD4NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--multipart-after
HTTP 200


# benign-09--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsinKzy+xs8ksSc21gwja6IM5NvpgGaXY2loAeXRnPUEAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--gzip-after
HTTP 200


# benign-09--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbIpys8vsbPJLEnNtYMI2uiDOTb6YBml2NpaAONGFsE=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--deflate-after
HTTP 200


# benign-09--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-09--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-09--br
Content-Type: application/json
Content-Encoding: br
base64,G0AAEKyOtyFjxyGi4yKY71XKvS2R7OviKRadFuHxKdsiL2lyWqTtjQ5M5AS8wL7A8LABJxIUbAMizpz5qOvVd2bSzBk=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-09--br-after
HTTP 200


# benign-10--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--query-before
HTTP 200

GET {{base_url}}/probe?q=user%40example.com
X-Hurl-Case: benign-10--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--query-after
HTTP 200


# benign-10--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--query-double-before
HTTP 200

GET {{base_url}}/probe?q=user%2540example.com
X-Hurl-Case: benign-10--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--query-double-after
HTTP 200


# benign-10--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=user%40example.com
X-Hurl-Case: benign-10--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--query-duplicate-after
HTTP 200


# benign-10--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-10--header
X-Probe: user%40example.com
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--header-after
HTTP 200


# benign-10--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-10--cookie
Cookie: probe=user%40example.com
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--cookie-after
HTTP 200


# benign-10--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--form
Content-Type: application/x-www-form-urlencoded
```
q=user%40example.com
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--form-after
HTTP 200


# benign-10--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--json
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--json-after
HTTP 200


# benign-10--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-10--json-put
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--json-put-after
HTTP 200


# benign-10--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-10--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--json-patch-after
HTTP 200


# benign-10--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--text
Content-Type: text/plain
```
user@example.com
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--text-after
HTTP 200


# benign-10--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--xml
Content-Type: application/xml
```
<root><value>user@example.com</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--xml-after
HTTP 200


# benign-10--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KdXNlckBleGFtcGxlLmNvbQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--multipart-after
HTTP 200


# benign-10--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRKi1OLXJIrUjMLchJ1UvOz1WKra0FAPH/GtMxAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--gzip-after
HTTP 200


# benign-10--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUSotTi1ySK1IzC3ISdVLzs9Viq2tBQCnvRFk;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--deflate-after
HTTP 200


# benign-10--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-10--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-10--br
Content-Type: application/json
Content-Encoding: br
base64,GzAAiBwHbqzFThB800eo9BbZ2wKaOixd0QcbcOAQL2NcYUwdMrliHiJCHFTGVs5tBw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-10--br-after
HTTP 200


# benign-sql--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--query-before
HTTP 200

GET {{base_url}}/probe?q=The%20UNION%20committee%20will%20select%20a%20chair
X-Hurl-Case: benign-sql--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--query-after
HTTP 200


# benign-sql--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--query-double-before
HTTP 200

GET {{base_url}}/probe?q=The%2520UNION%2520committee%2520will%2520select%2520a%2520chair
X-Hurl-Case: benign-sql--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--query-double-after
HTTP 200


# benign-sql--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=The%20UNION%20committee%20will%20select%20a%20chair
X-Hurl-Case: benign-sql--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--query-duplicate-after
HTTP 200


# benign-sql--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-sql--header
X-Probe: The%20UNION%20committee%20will%20select%20a%20chair
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--header-after
HTTP 200


# benign-sql--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-sql--cookie
Cookie: probe=The%20UNION%20committee%20will%20select%20a%20chair
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--cookie-after
HTTP 200


# benign-sql--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--form
Content-Type: application/x-www-form-urlencoded
```
q=The%20UNION%20committee%20will%20select%20a%20chair
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--form-after
HTTP 200


# benign-sql--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--json
Content-Type: application/json
```
{"data":{"values":["normal","The UNION committee will select a chair"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--json-after
HTTP 200


# benign-sql--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-sql--json-put
Content-Type: application/json
```
{"data":{"values":["normal","The UNION committee will select a chair"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--json-put-after
HTTP 200


# benign-sql--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-sql--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","The UNION committee will select a chair"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--json-patch-after
HTTP 200


# benign-sql--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--text
Content-Type: text/plain
```
The UNION committee will select a chair
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--text-after
HTTP 200


# benign-sql--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--xml
Content-Type: application/xml
```
<root><value>The UNION committee will select a chair</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--xml-after
HTTP 200


# benign-sql--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KVGhlIFVOSU9OIGNvbW1pdHRlZSB3aWxsIHNlbGVjdCBhIGNoYWlyDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--multipart-after
HTTP 200


# benign-sql--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRCslIVQj18/T3U0jOz83NLClJTVUoz8zJUShOzUlNLlFIVEjOSMwsUoqtrQUA+E5tHUgAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--gzip-after
HTTP 200


# benign-sql--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUQrJSFUI9fP091NIzs/NzSwpSU1VKM/MyVEoTs1JTS5RSFRIzkjMLFKKra0FAHjhGN0=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--deflate-after
HTTP 200


# benign-sql--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-sql--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-sql--br
Content-Type: application/json
Content-Encoding: br
base64,G0cAgB2HcSyyjaI8bTRBLbK3RTV1WLqyD63MY2zsj/KNKeMBD1mpskkPKolGgsc5TnY/aJpIdBJtHCTBNQ==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-sql--br-after
HTTP 200


# benign-command--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--query-before
HTTP 200

GET {{base_url}}/probe?q=Documentation%3A%20curl%20downloads%20a%20URL
X-Hurl-Case: benign-command--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--query-after
HTTP 200


# benign-command--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Documentation%253A%2520curl%2520downloads%2520a%2520URL
X-Hurl-Case: benign-command--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--query-double-after
HTTP 200


# benign-command--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Documentation%3A%20curl%20downloads%20a%20URL
X-Hurl-Case: benign-command--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--query-duplicate-after
HTTP 200


# benign-command--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-command--header
X-Probe: Documentation%3A%20curl%20downloads%20a%20URL
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--header-after
HTTP 200


# benign-command--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-command--cookie
Cookie: probe=Documentation%3A%20curl%20downloads%20a%20URL
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--cookie-after
HTTP 200


# benign-command--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--form
Content-Type: application/x-www-form-urlencoded
```
q=Documentation%3A%20curl%20downloads%20a%20URL
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--form-after
HTTP 200


# benign-command--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--json
Content-Type: application/json
```
{"data":{"values":["normal","Documentation: curl downloads a URL"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--json-after
HTTP 200


# benign-command--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-command--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Documentation: curl downloads a URL"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--json-put-after
HTTP 200


# benign-command--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-command--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Documentation: curl downloads a URL"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--json-patch-after
HTTP 200


# benign-command--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--text
Content-Type: text/plain
```
Documentation: curl downloads a URL
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--text-after
HTTP 200


# benign-command--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--xml
Content-Type: application/xml
```
<root><value>Documentation: curl downloads a URL</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--xml-after
HTTP 200


# benign-command--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KRG9jdW1lbnRhdGlvbjogY3VybCBkb3dubG9hZHMgYSBVUkwNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--multipart-after
HTTP 200


# benign-command--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRcslPLs1NzStJLMnMz7NSSC4tylFIyS/Py8lPTClWSFQIDfJRiq2tBQDyM+b4RAAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--gzip-after
HTTP 200


# benign-command--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUXLJTy7NTc0rSSzJzM+zUkguLcpRSMkvz8vJT0wpVkhUCA3yUYqtrQUAM1QX9Q==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--deflate-after
HTTP 200


# benign-command--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-command--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-command--br
Content-Type: application/json
Content-Encoding: br
base64,G0MAgK0K7Db0J00wmNXOhTjMIztCpbfI3hbTrOoaKxad7KuTahARHTYlIgcbcAIecEDJxAemRSRYDdGI9REvfWnjz/iwmgA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-command--br-after
HTTP 200


# benign-code--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--query-before
HTTP 200

GET {{base_url}}/probe?q=The%20eval%20function%20is%20not%20used.
X-Hurl-Case: benign-code--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--query-after
HTTP 200


# benign-code--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--query-double-before
HTTP 200

GET {{base_url}}/probe?q=The%2520eval%2520function%2520is%2520not%2520used.
X-Hurl-Case: benign-code--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--query-double-after
HTTP 200


# benign-code--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=The%20eval%20function%20is%20not%20used.
X-Hurl-Case: benign-code--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--query-duplicate-after
HTTP 200


# benign-code--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-code--header
X-Probe: The%20eval%20function%20is%20not%20used.
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--header-after
HTTP 200


# benign-code--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-code--cookie
Cookie: probe=The%20eval%20function%20is%20not%20used.
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--cookie-after
HTTP 200


# benign-code--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--form
Content-Type: application/x-www-form-urlencoded
```
q=The%20eval%20function%20is%20not%20used.
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--form-after
HTTP 200


# benign-code--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--json
Content-Type: application/json
```
{"data":{"values":["normal","The eval function is not used."]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--json-after
HTTP 200


# benign-code--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-code--json-put
Content-Type: application/json
```
{"data":{"values":["normal","The eval function is not used."]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--json-put-after
HTTP 200


# benign-code--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-code--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","The eval function is not used."]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--json-patch-after
HTTP 200


# benign-code--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--text
Content-Type: text/plain
```
The eval function is not used.
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--text-after
HTTP 200


# benign-code--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--xml
Content-Type: application/xml
```
<root><value>The eval function is not used.</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--xml-after
HTTP 200


# benign-code--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KVGhlIGV2YWwgZnVuY3Rpb24gaXMgbm90IHVzZWQuDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--multipart-after
HTTP 200


# benign-code--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRCslIVUgtS8xRSCvNSy7JzM9TyCxWyMsvUSgtTk3RU4qtrQUAVvtjBD8AAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--gzip-after
HTTP 200


# benign-code--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUQrJSFVILUvMUUgrzUsuyczPU8gsVsjLL1EoLU5N0VOKra0FALH0Fec=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--deflate-after
HTTP 200


# benign-code--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-code--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-code--br
Content-Type: application/json
Content-Encoding: br
base64,Gz4AEJwHtnPknfRTKHx6U9JwhEpvkT26rYnm9pZ9XVTFogMOOXD7PAzbqaQQi8sOkrQETdThkSzFIt/+H2cG;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-code--br-after
HTTP 200


# benign-xxe--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-before
HTTP 200

GET {{base_url}}/probe?q=%3CREPORT%3E%3CENTITY%3ECustomer%3C%2FENTITY%3E%3C%2FREPORT%3E
X-Hurl-Case: benign-xxe--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-after
HTTP 200


# benign-xxe--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253CREPORT%253E%253CENTITY%253ECustomer%253C%252FENTITY%253E%253C%252FREPORT%253E
X-Hurl-Case: benign-xxe--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-double-after
HTTP 200


# benign-xxe--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3CREPORT%3E%3CENTITY%3ECustomer%3C%2FENTITY%3E%3C%2FREPORT%3E
X-Hurl-Case: benign-xxe--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--query-duplicate-after
HTTP 200


# benign-xxe--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-xxe--header
X-Probe: %3CREPORT%3E%3CENTITY%3ECustomer%3C%2FENTITY%3E%3C%2FREPORT%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--header-after
HTTP 200


# benign-xxe--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-xxe--cookie
Cookie: probe=%3CREPORT%3E%3CENTITY%3ECustomer%3C%2FENTITY%3E%3C%2FREPORT%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--cookie-after
HTTP 200


# benign-xxe--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--form
Content-Type: application/x-www-form-urlencoded
```
q=%3CREPORT%3E%3CENTITY%3ECustomer%3C%2FENTITY%3E%3C%2FREPORT%3E
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--form-after
HTTP 200


# benign-xxe--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--json
Content-Type: application/json
```
{"data":{"values":["normal","<REPORT><ENTITY>Customer</ENTITY></REPORT>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-after
HTTP 200


# benign-xxe--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-xxe--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<REPORT><ENTITY>Customer</ENTITY></REPORT>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-put-after
HTTP 200


# benign-xxe--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-xxe--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<REPORT><ENTITY>Customer</ENTITY></REPORT>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--json-patch-after
HTTP 200


# benign-xxe--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--text
Content-Type: text/plain
```
<REPORT><ENTITY>Customer</ENTITY></REPORT>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--text-after
HTTP 200


# benign-xxe--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--xml
Content-Type: application/xml
```
<root><value>&lt;REPORT&gt;&lt;ENTITY&gt;Customer&lt;/ENTITY&gt;&lt;/REPORT&gt;</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--xml-after
HTTP 200


# benign-xxe--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPFJFUE9SVD48RU5USVRZPkN1c3RvbWVyPC9FTlRJVFk+PC9SRVBPUlQ+DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--multipart-after
HTTP 200


# benign-xxe--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsglyDfAPCrGzcfUL8QyJtHMuLS7Jz00tstGHCtjoQ1UoxdbWAgBPQA8WSwAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--gzip-after
HTTP 200


# benign-xxe--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbIJcg3wDwqxs3H1C/EMibRzLi0uyc9NLbLRhwrY6ENVKMXW1gIAnqEYFg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--deflate-after
HTTP 200


# benign-xxe--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-xxe--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-xxe--br
Content-Type: application/json
Content-Encoding: br
base64,G0oAEKyOdxNeaAwCqlXeTwSUe1sixdNVLDotwvNStJz62ieJKgso4wR8NZA34cMGnEiwZKssPCApcxr5CH66bHaTLdhkDg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-xxe--br-after
HTTP 200


# benign-file--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--query-before
HTTP 200

GET {{base_url}}/probe?q=Upload%20the%20hosts%20document%20from%20your%20desktop
X-Hurl-Case: benign-file--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--query-after
HTTP 200


# benign-file--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Upload%2520the%2520hosts%2520document%2520from%2520your%2520desktop
X-Hurl-Case: benign-file--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--query-double-after
HTTP 200


# benign-file--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Upload%20the%20hosts%20document%20from%20your%20desktop
X-Hurl-Case: benign-file--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--query-duplicate-after
HTTP 200


# benign-file--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-file--header
X-Probe: Upload%20the%20hosts%20document%20from%20your%20desktop
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--header-after
HTTP 200


# benign-file--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-file--cookie
Cookie: probe=Upload%20the%20hosts%20document%20from%20your%20desktop
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--cookie-after
HTTP 200


# benign-file--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--form
Content-Type: application/x-www-form-urlencoded
```
q=Upload%20the%20hosts%20document%20from%20your%20desktop
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--form-after
HTTP 200


# benign-file--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--json
Content-Type: application/json
```
{"data":{"values":["normal","Upload the hosts document from your desktop"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--json-after
HTTP 200


# benign-file--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-file--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Upload the hosts document from your desktop"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--json-put-after
HTTP 200


# benign-file--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-file--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Upload the hosts document from your desktop"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--json-patch-after
HTTP 200


# benign-file--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--text
Content-Type: text/plain
```
Upload the hosts document from your desktop
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--text-after
HTTP 200


# benign-file--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--xml
Content-Type: application/xml
```
<root><value>Upload the hosts document from your desktop</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--xml-after
HTTP 200


# benign-file--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KVXBsb2FkIHRoZSBob3N0cyBkb2N1bWVudCBmcm9tIHlvdXIgZGVza3RvcA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--multipart-after
HTTP 200


# benign-file--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBwQnAIAwF0FXCP3cC9+ip9BBqilA1YmKhiLv3vYnIzggTL+chhnCgai+csWFvWTmSJ6Gk5kZRr1GkOt1dC306OkWxx7XhXOsHVMCsSUwAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--gzip-after
HTTP 200


# benign-file--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwcEJwCAMBdBVwj93AvfoqfQQaopQNWJioYi7972JyM4IEy/nIYZwoGovnLFhb1k5kiehpOZGUa9RpDrdXQt9OjpFsce14VzrBwAaG38=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--deflate-after
HTTP 200


# benign-file--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-file--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-file--br
Content-Type: application/json
Content-Encoding: br
base64,G0sAEJwHdixnOoKwtP1SZGsvdVi6oi9FQObAIQfsDywIWphTTiTNwgAjTpLvHpyJJ3QyD3EumfuakpEgqiEd;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-file--br-after
HTTP 200


# benign-deser--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--query-before
HTTP 200

GET {{base_url}}/probe?q=Serialization%20format%3A%20JSON
X-Hurl-Case: benign-deser--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--query-after
HTTP 200


# benign-deser--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Serialization%2520format%253A%2520JSON
X-Hurl-Case: benign-deser--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--query-double-after
HTTP 200


# benign-deser--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Serialization%20format%3A%20JSON
X-Hurl-Case: benign-deser--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--query-duplicate-after
HTTP 200


# benign-deser--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-deser--header
X-Probe: Serialization%20format%3A%20JSON
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--header-after
HTTP 200


# benign-deser--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-deser--cookie
Cookie: probe=Serialization%20format%3A%20JSON
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--cookie-after
HTTP 200


# benign-deser--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--form
Content-Type: application/x-www-form-urlencoded
```
q=Serialization%20format%3A%20JSON
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--form-after
HTTP 200


# benign-deser--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--json
Content-Type: application/json
```
{"data":{"values":["normal","Serialization format: JSON"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--json-after
HTTP 200


# benign-deser--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-deser--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Serialization format: JSON"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--json-put-after
HTTP 200


# benign-deser--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-deser--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Serialization format: JSON"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--json-patch-after
HTTP 200


# benign-deser--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--text
Content-Type: text/plain
```
Serialization format: JSON
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--text-after
HTTP 200


# benign-deser--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--xml
Content-Type: application/xml
```
<root><value>Serialization format: JSON</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--xml-after
HTTP 200


# benign-deser--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KU2VyaWFsaXphdGlvbiBmb3JtYXQ6IEpTT04NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--multipart-after
HTTP 200


# benign-deser--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRCk4tykzMyaxKLMnMz1NIAwmXWCl4Bfv7KcXW1gIAIVnGozsAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--gzip-after
HTTP 200


# benign-deser--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUQpOLcpMzMmsSizJzM9TSAMJl1gpeAX7+ynF1tYCAGbQFKc=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--deflate-after
HTTP 200


# benign-deser--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-deser--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-deser--br
Content-Type: application/json
Content-Encoding: br
base64,GzoAiJwHdgyZBlpYmNM5Dv4RKr1F9sK2Zl/XVOuIsKStMjlwyIHLKQywXbjC+DpkOtE0SAQz8Jr8TyWIzMwA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-deser--br-after
HTTP 200


# benign-directory--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--query-before
HTTP 200

GET {{base_url}}/probe?q=%2Fdocs%2F..%2Fguide
X-Hurl-Case: benign-directory--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--query-after
HTTP 200


# benign-directory--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%252Fdocs%252F..%252Fguide
X-Hurl-Case: benign-directory--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--query-double-after
HTTP 200


# benign-directory--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%2Fdocs%2F..%2Fguide
X-Hurl-Case: benign-directory--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--query-duplicate-after
HTTP 200


# benign-directory--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-directory--header
X-Probe: %2Fdocs%2F..%2Fguide
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--header-after
HTTP 200


# benign-directory--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-directory--cookie
Cookie: probe=%2Fdocs%2F..%2Fguide
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--cookie-after
HTTP 200


# benign-directory--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--form
Content-Type: application/x-www-form-urlencoded
```
q=%2Fdocs%2F..%2Fguide
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--form-after
HTTP 200


# benign-directory--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--json
Content-Type: application/json
```
{"data":{"values":["normal","/docs/../guide"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--json-after
HTTP 200


# benign-directory--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-directory--json-put
Content-Type: application/json
```
{"data":{"values":["normal","/docs/../guide"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--json-put-after
HTTP 200


# benign-directory--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-directory--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","/docs/../guide"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--json-patch-after
HTTP 200


# benign-directory--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--text
Content-Type: text/plain
```
/docs/../guide
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--text-after
HTTP 200


# benign-directory--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--xml
Content-Type: application/xml
```
<root><value>/docs/../guide</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--xml-after
HTTP 200


# benign-directory--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KL2RvY3MvLi4vZ3VpZGUNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--multipart-after
HTTP 200


# benign-directory--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR0k/JTy7W19PTTy/NTElViq2tBQAT+y6ALwAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--gzip-after
HTTP 200


# benign-directory--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUdJPyU8u1tfT008vzUxJVYqtrQUAeBAPrA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--deflate-after
HTTP 200


# benign-directory--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-directory--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-directory--br
Content-Type: application/json
Content-Encoding: br
base64,CxeAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCIvZG9jcy8uLi9ndWlkZSJdfX0D;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-directory--br-after
HTTP 200


# benign-template--query | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--query-before
HTTP 200

GET {{base_url}}/probe?q=Template%20example%3A%207%20multiplied%20by%207
X-Hurl-Case: benign-template--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--query-after
HTTP 200


# benign-template--query-double | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Template%2520example%253A%25207%2520multiplied%2520by%25207
X-Hurl-Case: benign-template--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--query-double-after
HTTP 200


# benign-template--query-duplicate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Template%20example%3A%207%20multiplied%20by%207
X-Hurl-Case: benign-template--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--query-duplicate-after
HTTP 200


# benign-template--header | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-template--header
X-Probe: Template%20example%3A%207%20multiplied%20by%207
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--header-after
HTTP 200


# benign-template--cookie | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: benign-template--cookie
Cookie: probe=Template%20example%3A%207%20multiplied%20by%207
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--cookie-after
HTTP 200


# benign-template--form | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--form
Content-Type: application/x-www-form-urlencoded
```
q=Template%20example%3A%207%20multiplied%20by%207
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--form-after
HTTP 200


# benign-template--json | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--json
Content-Type: application/json
```
{"data":{"values":["normal","Template example: 7 multiplied by 7"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--json-after
HTTP 200


# benign-template--json-put | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: benign-template--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Template example: 7 multiplied by 7"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--json-put-after
HTTP 200


# benign-template--json-patch | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: benign-template--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Template example: 7 multiplied by 7"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--json-patch-after
HTTP 200


# benign-template--text | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--text
Content-Type: text/plain
```
Template example: 7 multiplied by 7
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--text-after
HTTP 200


# benign-template--xml | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--xml
Content-Type: application/xml
```
<root><value>Template example: 7 multiplied by 7</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--xml-after
HTTP 200


# benign-template--multipart | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KVGVtcGxhdGUgZXhhbXBsZTogNyBtdWx0aXBsaWVkIGJ5IDcNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--multipart-after
HTTP 200


# benign-template--gzip | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBMQqAMAwF0KuEP7sXcg43cYg0QyGpQVtRSu/uewNZmoAHHrGuN3hDPS8Xw4JVPUyakr7iYcqUyLu1ElY00/FRwj7nD49nmr9EAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--gzip-after
HTTP 200


# benign-template--deflate | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwTEKgDAMBdCrhD+7F3ION3GINEMhqUFbUUrv7nsDWZqABx6xrjd4Qz0vF8OCVT1MmpK+4mHKlMi7tRJWNNPxUcI+5w8ouheQ;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--deflate-after
HTTP 200


# benign-template--br | benign | LyWaf corpus 1.1.0
GET {{base_url}}/
X-Hurl-Case: benign-template--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: benign-template--br
Content-Type: application/json
Content-Encoding: br
base64,G0MAgC0OeHPIlTRgZgbt5SPUIiBbe+tGLPvyuopFp0V0yMqAQw6cAmketEPKgUMOLhK3JEvyCV4wjiMYCVT3MT0f7joV;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: benign-template--br-after
HTTP 200


# normal-apostrophe--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-before
HTTP 200

GET {{base_url}}/probe?q=O%27Reilly
X-Hurl-Case: normal-apostrophe--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-after
HTTP 200


# normal-apostrophe--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-double-before
HTTP 200

GET {{base_url}}/probe?q=O%2527Reilly
X-Hurl-Case: normal-apostrophe--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-double-after
HTTP 200


# normal-apostrophe--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=O%27Reilly
X-Hurl-Case: normal-apostrophe--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--query-duplicate-after
HTTP 200


# normal-apostrophe--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--header
X-Probe: O%27Reilly
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--header-after
HTTP 200


# normal-apostrophe--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--cookie
Cookie: probe=O%27Reilly
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--cookie-after
HTTP 200


# normal-apostrophe--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--form
Content-Type: application/x-www-form-urlencoded
```
q=O%27Reilly
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--form-after
HTTP 200


# normal-apostrophe--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--json
Content-Type: application/json
```
{"data":{"values":["normal","O'Reilly"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-after
HTTP 200


# normal-apostrophe--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--json-put
Content-Type: application/json
```
{"data":{"values":["normal","O'Reilly"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-put-after
HTTP 200


# normal-apostrophe--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","O'Reilly"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--json-patch-after
HTTP 200


# normal-apostrophe--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--text
Content-Type: text/plain
```
O'Reilly
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--text-after
HTTP 200


# normal-apostrophe--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--xml
Content-Type: application/xml
```
<root><value>O'Reilly</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--xml-after
HTTP 200


# normal-apostrophe--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KTydSZWlsbHkNCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--multipart-after
HTTP 200


# normal-apostrophe--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJR8lcPSs3MyalUiq2tBQAgXp2CKQAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--gzip-after
HTTP 200


# normal-apostrophe--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUfJXD0rNzMmpVIqtrQUAIhYN8w==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--deflate-after
HTTP 200


# normal-apostrophe--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-apostrophe--br
Content-Type: application/json
Content-Encoding: br
base64,CxSAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJPJ1JlaWxseSJdfX0D;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-apostrophe--br-after
HTTP 200


# normal-unicode--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-before
HTTP 200

GET {{base_url}}/probe?q=%E6%AD%A3%E5%B8%B8%E6%90%9C%E7%B4%A2%E5%86%85%E5%AE%B9
X-Hurl-Case: normal-unicode--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-after
HTTP 200


# normal-unicode--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%25E6%25AD%25A3%25E5%25B8%25B8%25E6%2590%259C%25E7%25B4%25A2%25E5%2586%2585%25E5%25AE%25B9
X-Hurl-Case: normal-unicode--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-double-after
HTTP 200


# normal-unicode--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%E6%AD%A3%E5%B8%B8%E6%90%9C%E7%B4%A2%E5%86%85%E5%AE%B9
X-Hurl-Case: normal-unicode--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--query-duplicate-after
HTTP 200


# normal-unicode--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-unicode--header
X-Probe: %E6%AD%A3%E5%B8%B8%E6%90%9C%E7%B4%A2%E5%86%85%E5%AE%B9
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--header-after
HTTP 200


# normal-unicode--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-unicode--cookie
Cookie: probe=%E6%AD%A3%E5%B8%B8%E6%90%9C%E7%B4%A2%E5%86%85%E5%AE%B9
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--cookie-after
HTTP 200


# normal-unicode--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--form
Content-Type: application/x-www-form-urlencoded
```
q=%E6%AD%A3%E5%B8%B8%E6%90%9C%E7%B4%A2%E5%86%85%E5%AE%B9
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--form-after
HTTP 200


# normal-unicode--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--json
Content-Type: application/json
```
{"data":{"values":["normal","正常搜索内容"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-after
HTTP 200


# normal-unicode--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-unicode--json-put
Content-Type: application/json
```
{"data":{"values":["normal","正常搜索内容"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-put-after
HTTP 200


# normal-unicode--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-unicode--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","正常搜索内容"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--json-patch-after
HTTP 200


# normal-unicode--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--text
Content-Type: text/plain
```
正常搜索内容
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--text-after
HTTP 200


# normal-unicode--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--xml
Content-Type: application/xml
```
<root><value>正常搜索内容</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--xml-after
HTTP 200


# normal-unicode--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0K5q2j5bi45pCc57Si5YaF5a65DQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--multipart-after
HTTP 200


# normal-unicode--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgEzAMz/eyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCLmraPluLjmkJzntKLlhoXlrrkiXX192xc3vTMAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--gzip-after
HTTP 200


# normal-unicode--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwBMwDM/3siZGF0YSI6eyJ2YWx1ZXMiOlsibm9ybWFsIiwi5q2j5bi45pCc57Si5YaF5a65Il19fR21GCI=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--deflate-after
HTTP 200


# normal-unicode--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-unicode--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-unicode--br
Content-Type: application/json
Content-Encoding: br
base64,GzIAiBwHbqzFThB802uRva6txaITfTCRA5eA4tMXbMCJpsn8sEEmkCOHp1EuI98B;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-unicode--br-after
HTTP 200


# normal-math--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--query-before
HTTP 200

GET {{base_url}}/probe?q=7%20*%207%20%3D%2049
X-Hurl-Case: normal-math--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--query-after
HTTP 200


# normal-math--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--query-double-before
HTTP 200

GET {{base_url}}/probe?q=7%2520*%25207%2520%253D%252049
X-Hurl-Case: normal-math--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--query-double-after
HTTP 200


# normal-math--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=7%20*%207%20%3D%2049
X-Hurl-Case: normal-math--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--query-duplicate-after
HTTP 200


# normal-math--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-math--header
X-Probe: 7%20*%207%20%3D%2049
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--header-after
HTTP 200


# normal-math--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-math--cookie
Cookie: probe=7%20*%207%20%3D%2049
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--cookie-after
HTTP 200


# normal-math--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--form
Content-Type: application/x-www-form-urlencoded
```
q=7%20*%207%20%3D%2049
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--form-after
HTTP 200


# normal-math--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--json
Content-Type: application/json
```
{"data":{"values":["normal","7 * 7 = 49"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--json-after
HTTP 200


# normal-math--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-math--json-put
Content-Type: application/json
```
{"data":{"values":["normal","7 * 7 = 49"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--json-put-after
HTTP 200


# normal-math--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-math--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","7 * 7 = 49"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--json-patch-after
HTTP 200


# normal-math--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--text
Content-Type: text/plain
```
7 * 7 = 49
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--text-after
HTTP 200


# normal-math--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--xml
Content-Type: application/xml
```
<root><value>7 * 7 = 49</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--xml-after
HTTP 200


# normal-math--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KNyAqIDcgPSA0OQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--multipart-after
HTTP 200


# normal-math--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRMlfQUjBXsFUwsVSKra0FAE5bE28rAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--gzip-after
HTTP 200


# normal-math--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUTJX0FIwV7BVMLFUiq2tBQAuVgzO;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--deflate-after
HTTP 200


# normal-math--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-math--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-math--br
Content-Type: application/json
Content-Encoding: br
base64,CxWAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI3ICogNyA9IDQ5Il19fQM=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-math--br-after
HTTP 200


# normal-url--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--query-before
HTTP 200

GET {{base_url}}/probe?q=https%3A%2F%2Fexample.com%2Fproducts
X-Hurl-Case: normal-url--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--query-after
HTTP 200


# normal-url--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--query-double-before
HTTP 200

GET {{base_url}}/probe?q=https%253A%252F%252Fexample.com%252Fproducts
X-Hurl-Case: normal-url--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--query-double-after
HTTP 200


# normal-url--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=https%3A%2F%2Fexample.com%2Fproducts
X-Hurl-Case: normal-url--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--query-duplicate-after
HTTP 200


# normal-url--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-url--header
X-Probe: https%3A%2F%2Fexample.com%2Fproducts
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--header-after
HTTP 200


# normal-url--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-url--cookie
Cookie: probe=https%3A%2F%2Fexample.com%2Fproducts
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--cookie-after
HTTP 200


# normal-url--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--form
Content-Type: application/x-www-form-urlencoded
```
q=https%3A%2F%2Fexample.com%2Fproducts
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--form-after
HTTP 200


# normal-url--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--json
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/products"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--json-after
HTTP 200


# normal-url--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-url--json-put
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/products"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--json-put-after
HTTP 200


# normal-url--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-url--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","https://example.com/products"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--json-patch-after
HTTP 200


# normal-url--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--text
Content-Type: text/plain
```
https://example.com/products
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--text-after
HTTP 200


# normal-url--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--xml
Content-Type: application/xml
```
<root><value>https://example.com/products</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--xml-after
HTTP 200


# normal-url--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KaHR0cHM6Ly9leGFtcGxlLmNvbS9wcm9kdWN0cw0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--multipart-after
HTTP 200


# normal-url--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRyigpKSi20tdPrUjMLchJ1UvOz9UvKMpPKU0uKVaKra0FACiFDII9AAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--gzip-after
HTTP 200


# normal-url--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUcooKSkottLXT61IzC3ISdVLzs/VLyjKTylNLilWiq2tBQCOkhXT;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--deflate-after
HTTP 200


# normal-url--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-url--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-url--br
Content-Type: application/json
Content-Encoding: br
base64,GzwAoB0HbqzFThB800uRrb3UYemKPtiAA9YlkHFOZXDIiZ5rSSBBHbJcieFkkillcyqg6Bs=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-url--br-after
HTTP 200


# normal-email--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--query-before
HTTP 200

GET {{base_url}}/probe?q=user%40example.com
X-Hurl-Case: normal-email--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--query-after
HTTP 200


# normal-email--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--query-double-before
HTTP 200

GET {{base_url}}/probe?q=user%2540example.com
X-Hurl-Case: normal-email--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--query-double-after
HTTP 200


# normal-email--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=user%40example.com
X-Hurl-Case: normal-email--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--query-duplicate-after
HTTP 200


# normal-email--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-email--header
X-Probe: user%40example.com
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--header-after
HTTP 200


# normal-email--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-email--cookie
Cookie: probe=user%40example.com
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--cookie-after
HTTP 200


# normal-email--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--form
Content-Type: application/x-www-form-urlencoded
```
q=user%40example.com
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--form-after
HTTP 200


# normal-email--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--json
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--json-after
HTTP 200


# normal-email--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-email--json-put
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--json-put-after
HTTP 200


# normal-email--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-email--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","user@example.com"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--json-patch-after
HTTP 200


# normal-email--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--text
Content-Type: text/plain
```
user@example.com
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--text-after
HTTP 200


# normal-email--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--xml
Content-Type: application/xml
```
<root><value>user@example.com</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--xml-after
HTTP 200


# normal-email--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KdXNlckBleGFtcGxlLmNvbQ0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--multipart-after
HTTP 200


# normal-email--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRKi1OLXJIrUjMLchJ1UvOz1WKra0FAPH/GtMxAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--gzip-after
HTTP 200


# normal-email--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUSotTi1ySK1IzC3ISdVLzs9Viq2tBQCnvRFk;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--deflate-after
HTTP 200


# normal-email--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-email--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-email--br
Content-Type: application/json
Content-Encoding: br
base64,GzAAiBwHbqzFThB800eo9BbZ2wKaOixd0QcbcOAQL2NcYUwdMrliHiJCHFTGVs5tBw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-email--br-after
HTTP 200


# normal-json--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--query-before
HTTP 200

GET {{base_url}}/probe?q=%7B%22name%22%3A%22Alice%22%2C%22count%22%3A2%7D
X-Hurl-Case: normal-json--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--query-after
HTTP 200


# normal-json--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%257B%2522name%2522%253A%2522Alice%2522%252C%2522count%2522%253A2%257D
X-Hurl-Case: normal-json--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--query-double-after
HTTP 200


# normal-json--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%7B%22name%22%3A%22Alice%22%2C%22count%22%3A2%7D
X-Hurl-Case: normal-json--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--query-duplicate-after
HTTP 200


# normal-json--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-json--header
X-Probe: %7B%22name%22%3A%22Alice%22%2C%22count%22%3A2%7D
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--header-after
HTTP 200


# normal-json--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-json--cookie
Cookie: probe=%7B%22name%22%3A%22Alice%22%2C%22count%22%3A2%7D
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--cookie-after
HTTP 200


# normal-json--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--form
Content-Type: application/x-www-form-urlencoded
```
q=%7B%22name%22%3A%22Alice%22%2C%22count%22%3A2%7D
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--form-after
HTTP 200


# normal-json--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--json
Content-Type: application/json
```
{"data":{"values":["normal","{\"name\":\"Alice\",\"count\":2}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--json-after
HTTP 200


# normal-json--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-json--json-put
Content-Type: application/json
```
{"data":{"values":["normal","{\"name\":\"Alice\",\"count\":2}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--json-put-after
HTTP 200


# normal-json--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-json--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","{\"name\":\"Alice\",\"count\":2}"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--json-patch-after
HTTP 200


# normal-json--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--text
Content-Type: text/plain
```
{"name":"Alice","count":2}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--text-after
HTTP 200


# normal-json--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--xml
Content-Type: application/xml
```
<root><value>{"name":"Alice","count":2}</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--xml-after
HTTP 200


# normal-json--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KeyJuYW1lIjoiQWxpY2UiLCJjb3VudCI6Mn0NCi0tSHVybEJvdW5kYXJ5LS0NCg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--multipart-after
HTTP 200


# normal-json--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACgXBMQ6AIAxA0bv8mcmxm+ewDg12MIGSKLgQ7u57k8u6IZPPyvAXOYj2VCskphJWXRFlL3d2JSm5jeiKbItzrR9J2+vJQQAAAA==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--gzip-after
HTTP 200


# normal-json--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJwFwTEOgCAMQNG7/JnJsZvnsA4NdjCBkii4EO7ue5PLuiGTz8rwFzmI9lQrJKYSVl0RZS93diUpuY3oimyLc60fx1IVcg==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--deflate-after
HTTP 200


# normal-json--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-json--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-json--br
Content-Type: application/json
Content-Encoding: br
base64,G0AAUIzTHfNNtiIjVDpR7ciXo/At4S5qssC+0vpgAw6YAx04oED0xPhoyRKWnTVULak2xNBxXlSs774YsSKZnQE=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-json--br-after
HTTP 200


# normal-xml--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--query-before
HTTP 200

GET {{base_url}}/probe?q=%3Croot%3E%3Cname%3EAlice%3C%2Fname%3E%3C%2Froot%3E
X-Hurl-Case: normal-xml--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--query-after
HTTP 200


# normal-xml--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--query-double-before
HTTP 200

GET {{base_url}}/probe?q=%253Croot%253E%253Cname%253EAlice%253C%252Fname%253E%253C%252Froot%253E
X-Hurl-Case: normal-xml--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--query-double-after
HTTP 200


# normal-xml--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=%3Croot%3E%3Cname%3EAlice%3C%2Fname%3E%3C%2Froot%3E
X-Hurl-Case: normal-xml--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--query-duplicate-after
HTTP 200


# normal-xml--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-xml--header
X-Probe: %3Croot%3E%3Cname%3EAlice%3C%2Fname%3E%3C%2Froot%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--header-after
HTTP 200


# normal-xml--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-xml--cookie
Cookie: probe=%3Croot%3E%3Cname%3EAlice%3C%2Fname%3E%3C%2Froot%3E
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--cookie-after
HTTP 200


# normal-xml--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--form
Content-Type: application/x-www-form-urlencoded
```
q=%3Croot%3E%3Cname%3EAlice%3C%2Fname%3E%3C%2Froot%3E
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--form-after
HTTP 200


# normal-xml--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--json
Content-Type: application/json
```
{"data":{"values":["normal","<root><name>Alice</name></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--json-after
HTTP 200


# normal-xml--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-xml--json-put
Content-Type: application/json
```
{"data":{"values":["normal","<root><name>Alice</name></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--json-put-after
HTTP 200


# normal-xml--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-xml--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","<root><name>Alice</name></root>"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--json-patch-after
HTTP 200


# normal-xml--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--text
Content-Type: text/plain
```
<root><name>Alice</name></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--text-after
HTTP 200


# normal-xml--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--xml
Content-Type: application/xml
```
<root><value>&lt;root&gt;&lt;name&gt;Alice&lt;/name&gt;&lt;/root&gt;</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--xml-after
HTTP 200


# normal-xml--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KPHJvb3Q+PG5hbWU+QWxpY2U8L25hbWU+PC9yb290Pg0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--multipart-after
HTTP 200


# normal-xml--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRsinKzy+xs8lLzE21c8zJTE610QezbfTBEkqxtbUAj7OFc0AAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--gzip-after
HTTP 200


# normal-xml--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUbIpys8vsbPJS8xNtXPMyUxOtdEHs230wRJKsbW1AMNzFfo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--deflate-after
HTTP 200


# normal-xml--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-xml--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-xml--br
Content-Type: application/json
Content-Encoding: br
base64,ix+AeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCI8cm9vdD48bmFtZT5BbGljZTwvbmFtZT48L3Jvb3Q+Il19fQM=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-xml--br-after
HTTP 200


# normal-sql-text--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-before
HTTP 200

GET {{base_url}}/probe?q=Please%20select%20a%20delivery%20method
X-Hurl-Case: normal-sql-text--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-after
HTTP 200


# normal-sql-text--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-double-before
HTTP 200

GET {{base_url}}/probe?q=Please%2520select%2520a%2520delivery%2520method
X-Hurl-Case: normal-sql-text--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-double-after
HTTP 200


# normal-sql-text--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=Please%20select%20a%20delivery%20method
X-Hurl-Case: normal-sql-text--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--query-duplicate-after
HTTP 200


# normal-sql-text--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-sql-text--header
X-Probe: Please%20select%20a%20delivery%20method
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--header-after
HTTP 200


# normal-sql-text--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-sql-text--cookie
Cookie: probe=Please%20select%20a%20delivery%20method
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--cookie-after
HTTP 200


# normal-sql-text--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--form
Content-Type: application/x-www-form-urlencoded
```
q=Please%20select%20a%20delivery%20method
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--form-after
HTTP 200


# normal-sql-text--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--json
Content-Type: application/json
```
{"data":{"values":["normal","Please select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-after
HTTP 200


# normal-sql-text--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-sql-text--json-put
Content-Type: application/json
```
{"data":{"values":["normal","Please select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-put-after
HTTP 200


# normal-sql-text--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-sql-text--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","Please select a delivery method"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--json-patch-after
HTTP 200


# normal-sql-text--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--text
Content-Type: text/plain
```
Please select a delivery method
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--text-after
HTTP 200


# normal-sql-text--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--xml
Content-Type: application/xml
```
<root><value>Please select a delivery method</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--xml-after
HTTP 200


# normal-sql-text--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KUGxlYXNlIHNlbGVjdCBhIGRlbGl2ZXJ5IG1ldGhvZA0KLS1IdXJsQm91bmRhcnktLQ0K;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--multipart-after
HTTP 200


# normal-sql-text--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRCshJTSxOVShOzUlNLlFIVEhJzcksSy2qVMhNLcnIT1GKra0FAP/6+39AAAAA;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--gzip-after
HTTP 200


# normal-sql-text--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUQrISU0sTlUoTs1JTS5RSFRISc3JLEstqlTITS3JyE9Riq2tBQDMPRas;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--deflate-after
HTTP 200


# normal-sql-text--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-sql-text--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-sql-text--br
Content-Type: application/json
Content-Encoding: br
base64,Gz8AgB0HbqzFThB802uRvbCtqcPSFX3gkAOX0iBP2sEhJ3puXYIaIMUwiYnUq8n/iPQ3;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-sql-text--br-after
HTTP 200


# normal-percent--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--query-before
HTTP 200

GET {{base_url}}/probe?q=discount%2020%25
X-Hurl-Case: normal-percent--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--query-after
HTTP 200


# normal-percent--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--query-double-before
HTTP 200

GET {{base_url}}/probe?q=discount%252020%2525
X-Hurl-Case: normal-percent--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--query-double-after
HTTP 200


# normal-percent--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=discount%2020%25
X-Hurl-Case: normal-percent--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--query-duplicate-after
HTTP 200


# normal-percent--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-percent--header
X-Probe: discount%2020%25
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--header-after
HTTP 200


# normal-percent--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-percent--cookie
Cookie: probe=discount%2020%25
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--cookie-after
HTTP 200


# normal-percent--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--form
Content-Type: application/x-www-form-urlencoded
```
q=discount%2020%25
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--form-after
HTTP 200


# normal-percent--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--json
Content-Type: application/json
```
{"data":{"values":["normal","discount 20%"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--json-after
HTTP 200


# normal-percent--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-percent--json-put
Content-Type: application/json
```
{"data":{"values":["normal","discount 20%"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--json-put-after
HTTP 200


# normal-percent--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-percent--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","discount 20%"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--json-patch-after
HTTP 200


# normal-percent--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--text
Content-Type: text/plain
```
discount 20%
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--text-after
HTTP 200


# normal-percent--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--xml
Content-Type: application/xml
```
<root><value>discount 20%</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--xml-after
HTTP 200


# normal-percent--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KZGlzY291bnQgMjAlDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--multipart-after
HTTP 200


# normal-percent--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRSsksTs4vzStRMDJQVYqtrQUAGQ7XKy0AAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--gzip-after
HTTP 200


# normal-percent--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrJLE7OL80rUTAyUFWKra0FAF+pDxw=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--deflate-after
HTTP 200


# normal-percent--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-percent--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-percent--br
Content-Type: application/json
Content-Encoding: br
base64,GywA+J0FdiybTl4Mn1bWQimytVcyXcWik30wJS2JJ6TIojHIAY5sMHJlFI93Ag==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-percent--br-after
HTTP 200


# normal-punctuation--query | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-before
HTTP 200

GET {{base_url}}/probe?q=a%2Bb%20%26%20c%3Dd
X-Hurl-Case: normal-punctuation--query
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-after
HTTP 200


# normal-punctuation--query-double | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-double-before
HTTP 200

GET {{base_url}}/probe?q=a%252Bb%2520%2526%2520c%253Dd
X-Hurl-Case: normal-punctuation--query-double
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-double-after
HTTP 200


# normal-punctuation--query-duplicate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-duplicate-before
HTTP 200

GET {{base_url}}/probe?q=normal&q=a%2Bb%20%26%20c%3Dd
X-Hurl-Case: normal-punctuation--query-duplicate
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--query-duplicate-after
HTTP 200


# normal-punctuation--header | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--header-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-punctuation--header
X-Probe: a%2Bb%20%26%20c%3Dd
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--header-after
HTTP 200


# normal-punctuation--cookie | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--cookie-before
HTTP 200

GET {{base_url}}/probe
X-Hurl-Case: normal-punctuation--cookie
Cookie: probe=a%2Bb%20%26%20c%3Dd
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--cookie-after
HTTP 200


# normal-punctuation--form | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--form-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--form
Content-Type: application/x-www-form-urlencoded
```
q=a%2Bb%20%26%20c%3Dd
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--form-after
HTTP 200


# normal-punctuation--json | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--json
Content-Type: application/json
```
{"data":{"values":["normal","a+b & c=d"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-after
HTTP 200


# normal-punctuation--json-put | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-put-before
HTTP 200

PUT {{base_url}}/probe
X-Hurl-Case: normal-punctuation--json-put
Content-Type: application/json
```
{"data":{"values":["normal","a+b & c=d"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-put-after
HTTP 200


# normal-punctuation--json-patch | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-patch-before
HTTP 200

PATCH {{base_url}}/probe
X-Hurl-Case: normal-punctuation--json-patch
Content-Type: application/json
```
{"data":{"values":["normal","a+b & c=d"]}}
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--json-patch-after
HTTP 200


# normal-punctuation--text | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--text-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--text
Content-Type: text/plain
```
a+b & c=d
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--text-after
HTTP 200


# normal-punctuation--xml | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--xml-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--xml
Content-Type: application/xml
```
<root><value>a+b &amp; c=d</value></root>
```
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--xml-after
HTTP 200


# normal-punctuation--multipart | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--multipart-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--multipart
Content-Type: multipart/form-data; boundary=HurlBoundary
base64,LS1IdXJsQm91bmRhcnkNCkNvbnRlbnQtRGlzcG9zaXRpb246IGZvcm0tZGF0YTsgbmFtZT0icSINCg0KYStiICYgYz1kDQotLUh1cmxCb3VuZGFyeS0tDQo=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--multipart-after
HTTP 200


# normal-punctuation--gzip | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--gzip-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--gzip
Content-Type: application/json
Content-Encoding: gzip
base64,H4sIAAAAAAAACqtWSkksSVSyqlYqS8wpTS1WsopWyssvyk3MUdJRStROUlBTSLZNUYqtrQUAgqoLGioAAAA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--gzip-after
HTTP 200


# normal-punctuation--deflate | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--deflate-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--deflate
Content-Type: application/json
Content-Encoding: deflate
base64,eJyrVkpJLElUsqpWKkvMKU0tVrKKVsrLL8pNzFHSUUrUTlJQU0i2TVGKra0FACk6DWQ=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--deflate-after
HTTP 200


# normal-punctuation--br | benign | Original benign control
GET {{base_url}}/
X-Hurl-Case: normal-punctuation--br-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: normal-punctuation--br
Content-Type: application/json
Content-Encoding: br
base64,ixSAeyJkYXRhIjp7InZhbHVlcyI6WyJub3JtYWwiLCJhK2IgJiBjPWQiXX19Aw==;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: normal-punctuation--br-after
HTTP 200


# edge-benign-large--raw | benign | Original transport/boundary probe
GET {{base_url}}/
X-Hurl-Case: edge-benign-large--raw-before
HTTP 200

POST {{base_url}}/probe
X-Hurl-Case: edge-benign-large--raw
Content-Type: text/plain
base64,bm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCBub3JtYWwgbm9ybWFsIG5vcm1hbCA=;
HTTP 200

GET {{base_url}}/
X-Hurl-Case: edge-benign-large--raw-after
HTTP 200


