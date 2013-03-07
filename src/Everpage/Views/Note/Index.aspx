<%@ Page Language="C#" Inherits="System.Web.Mvc.ViewPage<dynamic>" %>

<!DOCTYPE html>
<html>
<head>
    <meta http-equiv="Content-Type" content="text/html; charset=utf-8" />
    <title><%: Model.Title %></title>
</head>
<body>
    <!-- Took <%= Model.LoadTime.TotalMilliseconds %>ms to load from Evernote. -->
    <%= Model.Content %>
</body>
</html>