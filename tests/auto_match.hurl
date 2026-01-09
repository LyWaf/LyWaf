# 在 test_api.hurl 文件中
GET {{base_url}}/api/1
HTTP 400
GET {{base_url}}/api/cc/bbb
HTTP 400
GET {{base_url}}/api/cc/dd/bbb
HTTP 400
GET {{base_url}}/api/cc/dd/aaaa
HTTP 400

# 以下是检测攻击
GET {{base_url}}/?a=base64_decode(a,bv)
HTTP 403
GET {{base_url}}/?a=../
HTTP 403
GET {{base_url}}/?a=:$
HTTP 403
GET {{base_url}}/?a=$%7B
HTTP 403
GET {{base_url}}/?a=select%20from%201
HTTP 403
GET {{base_url}}/?a=union%20select%20
HTTP 403
GET {{base_url}}/?a=having
HTTP 403
GET {{base_url}}/?a=rongjitest
HTTP 403
GET {{base_url}}/?a=sleep(1)
HTTP 403
GET {{base_url}}/?a=benchmark(1,2)
HTTP 403
GET {{base_url}}/?a=base64_decode(1
HTTP 403
GET {{base_url}}/?a=SELECT%20table_name%20FROM%20information_schema.tables
HTTP 403
GET {{base_url}}/?a=etc/passwD
HTTP 403
GET {{base_url}}/?a=group%20by%20a(
HTTP 403
GET {{base_url}}/?a=xwork.MethodAccessor
HTTP 403
GET {{base_url}}/?a=alert%20%20(
HTTP 403
GET {{base_url}}/?a=%3C%20iframe
HTTP 403

POST {{base_url}}/
{"username": "etc/passwD", "password": "pass"}
HTTP 403

POST {{base_url}}/
Content-Type: application/x-www-form-urlencoded
[FormParams]
a: etc/passwD
b: xx

HTTP 403

POST {{base_url}}/
{"username": "user", "password": "pass"}
HTTP 400

GET {{base_url}}/?a=concat('sel','ect')
HTTP 403

GET {{base_url}}/?a=union%0aselect
HTTP 403