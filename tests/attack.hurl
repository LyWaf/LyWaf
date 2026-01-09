GET {{base_url}}
[Query]
a: union select
HTTP 403

GET {{base_url}}
[Query]
a: uni/*asaa*/on select
HTTP 403

GET {{base_url}}/?a=union%20select%20
HTTP 403

GET {{base_url}}/?a=union%0aselect
HTTP 403
