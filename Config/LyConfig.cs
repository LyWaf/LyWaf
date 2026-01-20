using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace LyWaf.Config;

/// <summary>
/// LyWaf 配置文件格式 (.ly) 解析器
/// 类似 Caddy 的简洁配置格式，支持变量和简单逻辑
/// </summary>
public class LyConfigParser
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 解析 .ly 配置文件并转换为字典结构
    /// </summary>
    public static Dictionary<string, object> Parse(string content, Dictionary<string, string>? variables = null)
    {
        var parser = new LyConfigParser();
        return parser.ParseContent(content, variables ?? []);
    }

    /// <summary>
    /// 从文件加载并解析配置
    /// </summary>
    public static Dictionary<string, object> ParseFile(string filePath, Dictionary<string, string>? variables = null)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"配置文件不存在: {filePath}");
        }

        var content = File.ReadAllText(filePath);
        return Parse(content, variables);
    }

    private readonly Dictionary<string, string> _variables = [];
    private readonly List<Token> _tokens = [];
    private int _position = 0;

    private Dictionary<string, object> ParseContent(string content, Dictionary<string, string> envVariables)
    {
        // 合并环境变量
        foreach (var kv in envVariables)
        {
            _variables[kv.Key] = kv.Value;
        }

        // 添加环境变量
        foreach (var env in Environment.GetEnvironmentVariables().Keys)
        {
            var key = env.ToString()!;
            if (!_variables.ContainsKey(key))
            {
                _variables[key] = Environment.GetEnvironmentVariable(key) ?? "";
            }
        }

        // 词法分析
        Tokenize(content);

        // 语法分析
        var result = ParseRoot();

        return result;
    }

    #region Lexer (词法分析)

    private void Tokenize(string content)
    {
        var lines = content.Split('\n');
        var lineNum = 0;

        foreach (var rawLine in lines)
        {
            lineNum++;
            var line = rawLine.Trim();

            // 跳过空行和注释
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            // 处理行内注释
            var commentIndex = line.IndexOf('#');
            if (commentIndex > 0 && !IsInString(line, commentIndex))
            {
                line = line[..commentIndex].Trim();
            }

            TokenizeLine(line, lineNum);
        }

        _tokens.Add(new Token(TokenType.EOF, "", lineNum));
    }

    private void TokenizeLine(string line, int lineNum)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];

            // 跳过空白
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // 特殊字符
            switch (c)
            {
                case '{':
                    _tokens.Add(new Token(TokenType.LeftBrace, "{", lineNum));
                    i++;
                    continue;
                case '}':
                    _tokens.Add(new Token(TokenType.RightBrace, "}", lineNum));
                    i++;
                    continue;
                case '[':
                    _tokens.Add(new Token(TokenType.LeftBracket, "[", lineNum));
                    i++;
                    continue;
                case ']':
                    _tokens.Add(new Token(TokenType.RightBracket, "]", lineNum));
                    i++;
                    continue;
                case ':':
                    // 检查是否是端口格式 :port（如 :5002）
                    if (i + 1 < line.Length && char.IsDigit(line[i + 1]))
                    {
                        var (portAddr, newPos) = ParsePortAddress(line, i);
                        _tokens.Add(new Token(TokenType.Identifier, portAddr, lineNum));
                        i = newPos;
                        continue;
                    }
                    _tokens.Add(new Token(TokenType.Colon, ":", lineNum));
                    i++;
                    continue;
                case ',':
                    _tokens.Add(new Token(TokenType.Comma, ",", lineNum));
                    i++;
                    continue;
                case '!':
                    // != 不等于运算符
                    if (i + 1 < line.Length && line[i + 1] == '=')
                    {
                        _tokens.Add(new Token(TokenType.NotEquals, "!=", lineNum));
                        i += 2;
                        continue;
                    }
                    // 单独的 ! 作为标识符的一部分（在正则表达式中会处理）
                    throw new LyConfigException($"未知字符 '{c}' 在第 {lineNum} 行，您是否想使用 '!=' ?");
                case '=':
                    // == 等于运算符
                    if (i + 1 < line.Length && line[i + 1] == '=')
                    {
                        _tokens.Add(new Token(TokenType.EqualsEquals, "==", lineNum));
                        i += 2;
                        continue;
                    }
                    _tokens.Add(new Token(TokenType.Equals, "=", lineNum));
                    i++;
                    continue;
                case ';':
                    // 忽略行末分号
                    i++;
                    continue;
            }

            // 字符串（带引号）
            if (c == '"' || c == '\'')
            {
                var (str, newPos) = ParseString(line, i, c);
                _tokens.Add(new Token(TokenType.String, str, lineNum));
                i = newPos;
                continue;
            }

            // 变量引用 ${var} 或 $var
            if (c == '$')
            {
                var (varName, newPos) = ParseVariable(line, i);
                _tokens.Add(new Token(TokenType.Variable, varName, lineNum));
                i = newPos;
                continue;
            }

            // 标识符或关键字
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/' || c == '*')
            {
                var (word, newPos) = ParseWord(line, i);
                var tokenType = word.ToLower() switch
                {
                    "true" or "false" => TokenType.Boolean,
                    "if" => TokenType.If,
                    "else" => TokenType.Else,
                    "var" or "let" => TokenType.VarDecl,
                    "import" => TokenType.Import,
                    _ when int.TryParse(word, out _) || double.TryParse(word, out _) => TokenType.Number,
                    _ => TokenType.Identifier
                };
                _tokens.Add(new Token(tokenType, word, lineNum));
                i = newPos;
                continue;
            }

            // 未知字符
            throw new LyConfigException($"未知字符 '{c}' 在第 {lineNum} 行");
        }

        // 行结束
        _tokens.Add(new Token(TokenType.NewLine, "\n", lineNum));
    }

    private static (string, int) ParseString(string line, int start, char quote)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                // 转义字符
                i++;
                sb.Append(line[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => line[i]
                });
            }
            else if (c == quote)
            {
                return (sb.ToString(), i + 1);
            }
            else
            {
                sb.Append(c);
            }
            i++;
        }
        throw new LyConfigException($"未闭合的字符串");
    }

    /// <summary>
    /// 解析端口地址格式 :port（如 :5002）
    /// </summary>
    private static (string, int) ParsePortAddress(string line, int start)
    {
        var sb = new StringBuilder();
        var i = start;
        
        // 添加冒号
        sb.Append(line[i]);
        i++;
        
        // 添加端口数字
        while (i < line.Length && char.IsDigit(line[i]))
        {
            sb.Append(line[i]);
            i++;
        }
        
        return (sb.ToString(), i);
    }

    private static (string, int) ParseVariable(string line, int start)
    {
        var i = start + 1;
        if (i < line.Length && line[i] == '{')
        {
            // ${var} 格式
            var end = line.IndexOf('}', i);
            if (end == -1)
                throw new LyConfigException("未闭合的变量引用");
            return (line[(i + 1)..end], end + 1);
        }
        else
        {
            // $var 格式
            var sb = new StringBuilder();
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
            {
                sb.Append(line[i]);
                i++;
            }
            return (sb.ToString(), i);
        }
    }

    private static (string, int) ParseWord(string line, int start)
    {
        var sb = new StringBuilder();
        var i = start;
        var parenDepth = 0;   // 圆括号深度计数 ()
        var bracketDepth = 0; // 方括号深度计数 []
        
        while (i < line.Length)
        {
            var c = line[i];
            var inRegex = parenDepth > 0 || bracketDepth > 0; // 是否在正则表达式内
            
            // 处理圆括号 - 当括号成对出现时作为字符串的一部分
            if (c == '(')
            {
                parenDepth++;
                sb.Append(c);
                i++;
                continue;
            }
            
            if (c == ')')
            {
                if (parenDepth > 0)
                {
                    parenDepth--;
                    sb.Append(c);
                    i++;
                    continue;
                }
                // 未匹配的右括号，停止解析
                break;
            }
            
            // 处理方括号 - 当括号成对出现时作为字符串的一部分
            if (c == '[')
            {
                bracketDepth++;
                sb.Append(c);
                i++;
                continue;
            }
            
            if (c == ']')
            {
                if (bracketDepth > 0)
                {
                    bracketDepth--;
                    sb.Append(c);
                    i++;
                    continue;
                }
                // 未匹配的右方括号，停止解析
                break;
            }
            
            // 正则表达式内的特殊字符允许：| ^ $ + ? { } ! < > =
            // ! 用于否定前瞻 (?!...) 等
            // < > = 用于命名捕获组 (?<name>...) 和断言 (?<=...) (?<!...)
            if (inRegex && (c == '|' || c == '^' || c == '$' || c == '+' || c == '?' || 
                c == '{' || c == '}' || c == '!' || c == '<' || c == '>' || c == '='))
            {
                sb.Append(c);
                i++;
                continue;
            }
            
            // 常规允许的字符
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/' || c == '*' || c == ':')
            {
                sb.Append(c);
                i++;
            }
            else
            {
                break;
            }
        }
        return (sb.ToString(), i);
    }

    private static bool IsInString(string line, int index)
    {
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < index; i++)
        {
            var c = line[i];
            if (!inString && (c == '"' || c == '\''))
            {
                inString = true;
                quote = c;
            }
            else if (inString && c == quote && (i == 0 || line[i - 1] != '\\'))
            {
                inString = false;
            }
        }
        return inString;
    }

    #endregion

    #region Parser (语法分析)

    private Token Current => _position < _tokens.Count ? _tokens[_position] : _tokens[^1];
    private Token Peek(int offset = 1) => _position + offset < _tokens.Count ? _tokens[_position + offset] : _tokens[^1];

    private Token Consume(TokenType expected)
    {
        if (Current.Type != expected)
            throw new LyConfigException($"期望 {expected}，但得到 {Current.Type} 在第 {Current.Line} 行");
        return _tokens[_position++];
    }

    private Token Consume()
    {
        return _tokens[_position++];
    }

    private bool Match(TokenType type)
    {
        if (Current.Type == type)
        {
            _position++;
            return true;
        }
        return false;
    }

    private void SkipNewLines()
    {
        while (Current.Type == TokenType.NewLine)
            _position++;
    }

    private Dictionary<string, object> ParseRoot()
    {
        var result = new Dictionary<string, object>();

        SkipNewLines();

        while (Current.Type != TokenType.EOF)
        {
            // 变量声明
            if (Current.Type == TokenType.VarDecl)
            {
                ParseVariableDeclaration();
                SkipNewLines();
                continue;
            }

            // import 语句
            if (Current.Type == TokenType.Import)
            {
                ParseImport(result);
                SkipNewLines();
                continue;
            }

            // 条件语句
            if (Current.Type == TokenType.If)
            {
                var conditionalBlock = ParseConditional();
                MergeDict(result, conditionalBlock);
                SkipNewLines();
                continue;
            }

            // 块定义或赋值
            if (Current.Type == TokenType.Identifier)
            {
                var (key, value) = ParseBlock();
                
                // 处理重复键（转为数组），支持 listen 等多值配置
                if (result.ContainsKey(key))
                {
                    if (result[key] is List<object> list)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        result[key] = new List<object> { result[key], value };
                    }
                }
                else
                {
                    result[key] = value;
                }
                
                SkipNewLines();
                continue;
            }

            // 处理顶级的字符串值（可能是赋值的一部分）
            if (Current.Type == TokenType.String || Current.Type == TokenType.Number || 
                Current.Type == TokenType.Boolean || Current.Type == TokenType.Variable)
            {
                // 跳过孤立的值
                ParseValue();
                SkipNewLines();
                continue;
            }

            // 处理等号（可能是前面已经消费了 key 的情况）
            if (Current.Type == TokenType.Equals || Current.Type == TokenType.Colon)
            {
                // 跳过孤立的等号/冒号
                Consume();
                if (Current.Type != TokenType.NewLine && Current.Type != TokenType.EOF)
                {
                    ParseValue();
                }
                SkipNewLines();
                continue;
            }

            // 处理花括号（if 语句块等）
            if (Current.Type == TokenType.LeftBrace)
            {
                Consume();
                SkipNewLines();
                var blockContent = ParseBlockContent();
                MergeDict(result, blockContent);
                if (Current.Type == TokenType.RightBrace)
                {
                    Consume();
                }
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.RightBrace)
            {
                // 跳过多余的右花括号
                Consume();
                SkipNewLines();
                continue;
            }

            // 处理方括号（数组）
            if (Current.Type == TokenType.LeftBracket)
            {
                ParseArray();
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.RightBracket)
            {
                Consume();
                SkipNewLines();
                continue;
            }

            // 处理逗号
            if (Current.Type == TokenType.Comma)
            {
                Consume();
                SkipNewLines();
                continue;
            }

            // 处理 else 关键字（可能是 if-else 结构的一部分）
            if (Current.Type == TokenType.Else)
            {
                Consume();
                SkipNewLines();
                // else 后面可能跟着 { 或 if
                if (Current.Type == TokenType.LeftBrace)
                {
                    Consume();
                    SkipNewLines();
                    var elseContent = ParseBlockContent();
                    MergeDict(result, elseContent);
                    if (Current.Type == TokenType.RightBrace)
                    {
                        Consume();
                    }
                }
                SkipNewLines();
                continue;
            }

            throw new LyConfigException($"意外的 token: {Current.Type} 在第 {Current.Line} 行");
        }

        return result;
    }

    private void ParseVariableDeclaration()
    {
        Consume(TokenType.VarDecl);
        var name = Consume(TokenType.Identifier).Value;
        Consume(TokenType.Equals);
        var value = ParseValue();
        _variables[name] = value?.ToString() ?? "";
    }

    private void ParseImport(Dictionary<string, object> result)
    {
        Consume(TokenType.Import);
        var path = ParseStringValue();
        
        if (File.Exists(path))
        {
            var importedContent = File.ReadAllText(path);
            var importedConfig = Parse(importedContent, _variables);
            MergeDict(result, importedConfig);
        }
        else
        {
            _logger.Warn("Import 文件不存在: {Path}", path);
        }
    }

    private Dictionary<string, object> ParseConditional()
    {
        Consume(TokenType.If);
        var condition = EvaluateCondition();
        SkipNewLines();

        Dictionary<string, object>? thenBlock = null;
        Dictionary<string, object>? elseBlock = null;

        if (Match(TokenType.LeftBrace))
        {
            SkipNewLines();
            thenBlock = ParseBlockContent();
            Consume(TokenType.RightBrace);
        }

        SkipNewLines();

        if (Match(TokenType.Else))
        {
            SkipNewLines();
            if (Match(TokenType.LeftBrace))
            {
                SkipNewLines();
                elseBlock = ParseBlockContent();
                Consume(TokenType.RightBrace);
            }
        }

        return condition ? (thenBlock ?? []) : (elseBlock ?? []);
    }

    private bool EvaluateCondition()
    {
        // 简单条件: 变量存在性检查或比较
        var left = ParseValue()?.ToString() ?? "";

        // 比较操作符 - 支持 == != 以及 eq ne
        if (Current.Type == TokenType.EqualsEquals)
        {
            Consume();
            var right = ParseValue()?.ToString() ?? "";
            return left == right;
        }
        else if (Current.Type == TokenType.NotEquals)
        {
            Consume();
            var right = ParseValue()?.ToString() ?? "";
            return left != right;
        }
        else if (Current.Type == TokenType.Identifier)
        {
            var op = Current.Value.ToLower();
            if (op == "eq")
            {
                Consume();
                var right = ParseValue()?.ToString() ?? "";
                return left == right;
            }
            else if (op == "ne")
            {
                Consume();
                var right = ParseValue()?.ToString() ?? "";
                return left != right;
            }
        }

        // 真值检查
        return !string.IsNullOrEmpty(left) && left.ToLower() != "false" && left != "0";
    }

    /// <summary>
    /// 已知的站点指令列表
    /// 这些指令在站点地址后遇到时，表示进入站点内容而不是参数
    /// </summary>
    private static readonly HashSet<string> SiteDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "respond", "file_server", "reverse_proxy", "proxy", "root", "browse", "index",
        "try_files", "header", "encode", "log", "route", "handle", "redir",
        "rewrite", "uri", "request_body", "templates", "tls", "import",
        "lb_policy", "load_balancing_policy", "health_check", "status",
        "content-type", "content_type", "charset", "show-req", "show_req",
        "basepath", "base_path", "precompressed", "pre_compressed",
        "max_file_size", "maxfilesize", "default"
    };

    private (string, object) ParseBlock()
    {
        var name = Consume(TokenType.Identifier).Value;
        SkipNewLines();

        // 简单赋值: key = value 或 key: value
        if (Current.Type == TokenType.Equals || Current.Type == TokenType.Colon)
        {
            Consume();
            var value = ParseValue();
            return (name, value ?? "");
        }

        // 如果当前 name 是站点地址
        if (IsSiteAddress(name))
        {
            // 收集逗号分隔的多个地址（如 localhost:5003, localhost:5004）
            var addresses = new List<string> { name };
            while (Current.Type == TokenType.Comma)
            {
                Consume(); // 消费逗号
                SkipNewLines();
                if (Current.Type == TokenType.Identifier && IsSiteAddress(Current.Value))
                {
                    addresses.Add(Consume(TokenType.Identifier).Value);
                    SkipNewLines();
                }
                else
                {
                    break;
                }
            }
            
            // 合并所有地址为一个空格分隔的字符串（Caddy 风格）
            var combinedAddress = string.Join(" ", addresses);

            // 检查下一个 token 是否是站点指令或路径块
            if (Current.Type == TokenType.Identifier)
            {
                var nextValue = Current.Value;
                if (nextValue.StartsWith('/') || SiteDirectives.Contains(nextValue))
                {
                    // 是站点内的指令或路径，进入非嵌套站点内容解析
                    var siteContent = ParseNonNestedSiteContent();
                    return (combinedAddress, siteContent);
                }
            }
            // 如果是 { 则进入嵌套块
            if (Current.Type == TokenType.LeftBrace)
            {
                Consume();
                SkipNewLines();
                var content = ParseBlockContent();
                Consume(TokenType.RightBrace);
                return (combinedAddress, content);
            }
            // 如果没有后续内容，返回空字典
            if (Current.Type == TokenType.NewLine || Current.Type == TokenType.EOF)
            {
                var siteContent = ParseNonNestedSiteContent();
                return (combinedAddress, siteContent);
            }
        }

        // 带参数的块: name arg1 arg2 { ... }
        // 注意：以 / 开头的 Identifier 是路径块，不是参数
        var args = new List<string>();
        while (Current.Type == TokenType.Identifier || Current.Type == TokenType.String || 
               Current.Type == TokenType.Number || Current.Type == TokenType.Variable)
        {
            // 如果是以 / 开头的标识符，它是路径块而不是参数，停止收集参数
            if (Current.Type == TokenType.Identifier && Current.Value.StartsWith('/'))
            {
                break;
            }
            // 如果是已知的站点指令，停止收集参数
            if (Current.Type == TokenType.Identifier && SiteDirectives.Contains(Current.Value))
            {
                break;
            }
            args.Add(ParseStringValue());
        }

        SkipNewLines();

        // 块内容
        if (Match(TokenType.LeftBrace))
        {
            SkipNewLines();
            var content = ParseBlockContent();
            Consume(TokenType.RightBrace);

            // 特殊处理：根据块名称决定结构
            return (name, ProcessBlock(name, args, content));
        }

        // 非嵌套站点配置：站点地址后面没有 {}，收集后续指令
        // 支持格式：
        // localhost
        // file_server
        // root = "./wwwroot"
        if (IsSiteAddress(name) && args.Count == 0)
        {
            var siteContent = ParseNonNestedSiteContent();
            return (name, siteContent);
        }

        // 单行块
        if (args.Count == 1)
        {
            return (name, args[0]);
        }
        else if (args.Count > 1)
        {
            return (name, args);
        }

        return (name, new Dictionary<string, object>());
    }

    /// <summary>
    /// 解析非嵌套的站点内容
    /// 收集后续指令直到遇到另一个站点地址、右花括号或文件结束
    /// </summary>
    private Dictionary<string, object> ParseNonNestedSiteContent()
    {
        var result = new Dictionary<string, object>();

        while (Current.Type != TokenType.EOF && Current.Type != TokenType.RightBrace)
        {
            SkipNewLines();

            if (Current.Type == TokenType.EOF || Current.Type == TokenType.RightBrace)
                break;

            // 检查是否是另一个站点地址（表示当前站点配置结束）
            if (Current.Type == TokenType.Identifier && IsSiteAddress(Current.Value))
            {
                // 预查看下一个 token，如果不是 = 或 :，则是新站点
                var nextPos = _position + 1;
                while (nextPos < _tokens.Count && _tokens[nextPos].Type == TokenType.NewLine)
                    nextPos++;
                
                if (nextPos < _tokens.Count)
                {
                    var nextType = _tokens[nextPos].Type;
                    if (nextType != TokenType.Equals && nextType != TokenType.Colon)
                    {
                        // 是新的站点地址，停止解析当前站点
                        break;
                    }
                }
            }

            // 变量声明
            if (Current.Type == TokenType.VarDecl)
            {
                ParseVariableDeclaration();
                SkipNewLines();
                continue;
            }

            // 条件语句
            if (Current.Type == TokenType.If)
            {
                var conditionalBlock = ParseConditional();
                MergeDict(result, conditionalBlock);
                SkipNewLines();
                continue;
            }

            // 块定义或赋值
            if (Current.Type == TokenType.Identifier)
            {
                var (key, value) = ParseBlock();
                
                if (result.ContainsKey(key))
                {
                    if (result[key] is List<object> list)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        result[key] = new List<object> { result[key], value };
                    }
                }
                else
                {
                    result[key] = value;
                }
                
                SkipNewLines();
                continue;
            }

            // 其他情况跳过
            if (Current.Type == TokenType.NewLine)
            {
                Consume();
                continue;
            }

            break;
        }

        return result;
    }

    /// <summary>
    /// 判断是否是站点地址格式
    /// 支持: example.com, :8080, https://example.com, localhost, *.example.com, 127.0.0.1
    /// </summary>
    private static bool IsSiteAddress(string key)
    {
        // 端口格式 :port
        if (key.StartsWith(':') && int.TryParse(key[1..], out _))
            return true;

        // URL 格式
        if (key.StartsWith("http://") || key.StartsWith("https://"))
            return true;

        // 域名格式 (包含 . 或 *)
        if (key.Contains('.') || key.StartsWith('*'))
            return true;

        var val = key.Split(':');
        // localhost
        if (val[0].Equals("localhost", StringComparison.CurrentCultureIgnoreCase)) {
            if(val.Length == 1 || int.TryParse(val[1], out _)) {
                return true;
            }
            return false;
        }
        // IP 地址格式
        if (System.Net.IPAddress.TryParse(val[0], out _)) {
            if(val.Length == 1 || int.TryParse(val[1], out _)) {
                return true;
            }
            return false;
        }
        return false;
    }

    private Dictionary<string, object> ParseBlockContent()
    {
        var result = new Dictionary<string, object>();

        while (Current.Type != TokenType.RightBrace && Current.Type != TokenType.EOF)
        {
            SkipNewLines();

            if (Current.Type == TokenType.RightBrace)
                break;

            if (Current.Type == TokenType.VarDecl)
            {
                ParseVariableDeclaration();
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.If)
            {
                var conditionalBlock = ParseConditional();
                MergeDict(result, conditionalBlock);
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.Identifier)
            {
                var (key, value) = ParseBlock();
                
                // 处理重复键（转为数组）
                if (result.ContainsKey(key))
                {
                    if (result[key] is List<object> list)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        result[key] = new List<object> { result[key], value };
                    }
                }
                else
                {
                    result[key] = value;
                }
                
                SkipNewLines();
                continue;
            }

            // 处理数字作为键的块（如 8080 { ... }）
            if (Current.Type == TokenType.Number)
            {
                var numKey = Consume().Value;
                SkipNewLines();
                
                // 检查是否跟着 { 表示是块定义
                if (Current.Type == TokenType.LeftBrace)
                {
                    Consume();
                    SkipNewLines();
                    var blockContent = ParseBlockContent();
                    Consume(TokenType.RightBrace);
                    
                    // 处理重复键
                    if (result.ContainsKey(numKey))
                    {
                        if (result[numKey] is List<object> list)
                        {
                            list.Add(blockContent);
                        }
                        else
                        {
                            result[numKey] = new List<object> { result[numKey], blockContent };
                        }
                    }
                    else
                    {
                        result[numKey] = blockContent;
                    }
                    
                    SkipNewLines();
                    continue;
                }
                // 数字后面跟着 = 或 : 表示是赋值
                else if (Current.Type == TokenType.Equals || Current.Type == TokenType.Colon)
                {
                    Consume();
                    var val = ParseValue();
                    if (val != null)
                    {
                        result[numKey] = val;
                    }
                    SkipNewLines();
                    continue;
                }
                // 否则就是孤立的数字值，忽略
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.NewLine)
            {
                SkipNewLines();
                continue;
            }

            // 处理孤立的值
            if (Current.Type == TokenType.String || 
                Current.Type == TokenType.Boolean || Current.Type == TokenType.Variable)
            {
                ParseValue();
                SkipNewLines();
                continue;
            }

            // 处理等号/冒号
            if (Current.Type == TokenType.Equals || Current.Type == TokenType.Colon)
            {
                Consume();
                if (Current.Type != TokenType.NewLine && Current.Type != TokenType.EOF && 
                    Current.Type != TokenType.RightBrace)
                {
                    ParseValue();
                }
                SkipNewLines();
                continue;
            }

            // 处理嵌套花括号
            if (Current.Type == TokenType.LeftBrace)
            {
                Consume();
                SkipNewLines();
                var nestedContent = ParseBlockContent();
                MergeDict(result, nestedContent);
                if (Current.Type == TokenType.RightBrace)
                {
                    Consume();
                }
                SkipNewLines();
                continue;
            }

            // 处理方括号
            if (Current.Type == TokenType.LeftBracket)
            {
                ParseArray();
                SkipNewLines();
                continue;
            }

            if (Current.Type == TokenType.RightBracket)
            {
                Consume();
                SkipNewLines();
                continue;
            }

            // 处理逗号
            if (Current.Type == TokenType.Comma)
            {
                Consume();
                SkipNewLines();
                continue;
            }

            // 处理 else 关键字
            if (Current.Type == TokenType.Else)
            {
                Consume();
                SkipNewLines();
                if (Current.Type == TokenType.LeftBrace)
                {
                    Consume();
                    SkipNewLines();
                    var elseContent = ParseBlockContent();
                    MergeDict(result, elseContent);
                    if (Current.Type == TokenType.RightBrace)
                    {
                        Consume();
                    }
                }
                SkipNewLines();
                continue;
            }

            throw new LyConfigException($"块内意外的 token: {Current.Type} 在第 {Current.Line} 行");
        }

        return result;
    }

    private object? ParseValue()
    {
        switch (Current.Type)
        {
            case TokenType.String:
                return ResolveVariables(Consume().Value);

            case TokenType.Number:
                var numStr = Consume().Value;
                if (int.TryParse(numStr, out var intVal))
                    return intVal;
                if (double.TryParse(numStr, out var doubleVal))
                    return doubleVal;
                return numStr;

            case TokenType.Boolean:
                return Consume().Value.ToLower() == "true";

            case TokenType.Variable:
                var varName = Consume().Value;
                return _variables.TryGetValue(varName, out var varValue) ? varValue : "";

            case TokenType.Identifier:
                return ResolveVariables(Consume().Value);

            case TokenType.LeftBracket:
                return ParseArray();

            case TokenType.LeftBrace:
                Consume();
                SkipNewLines();
                var content = ParseBlockContent();
                Consume(TokenType.RightBrace);
                return content;

            default:
                return null;
        }
    }

    private string ParseStringValue()
    {
        var value = ParseValue();
        return value?.ToString() ?? "";
    }

    private List<object> ParseArray()
    {
        Consume(TokenType.LeftBracket);
        var result = new List<object>();

        while (Current.Type != TokenType.RightBracket && Current.Type != TokenType.EOF)
        {
            SkipNewLines();

            if (Current.Type == TokenType.RightBracket)
                break;

            var value = ParseValue();
            if (value != null)
                result.Add(value);

            Match(TokenType.Comma);
            SkipNewLines();
        }

        Consume(TokenType.RightBracket);
        return result;
    }

    private string ResolveVariables(string input)
    {
        // 替换 ${var} 和 $var 格式的变量
        var result = Regex.Replace(input, @"\$\{([^}]+)\}", m =>
        {
            var varName = m.Groups[1].Value;
            return _variables.TryGetValue(varName, out var value) ? value : m.Value;
        });

        result = Regex.Replace(result, @"\$([a-zA-Z_][a-zA-Z0-9_]*)", m =>
        {
            var varName = m.Groups[1].Value;
            return _variables.TryGetValue(varName, out var value) ? value : m.Value;
        });

        return result;
    }

    private object ProcessBlock(string name, List<string> args, Dictionary<string, object> content)
    {
        // 根据块名称进行特殊处理
        return name.ToLower() switch
        {
            // 站点块
            "site" or "server" => ProcessSiteBlock(args, content),
            // 路由块
            "route" or "handle" => ProcessRouteBlock(args, content),
            // 上游块
            "upstream" or "backend" => ProcessUpstreamBlock(args, content),
            // 默认返回内容
            _ => content
        };
    }

    private Dictionary<string, object> ProcessSiteBlock(List<string> args, Dictionary<string, object> content)
    {
        var result = new Dictionary<string, object>(content);
        if (args.Count > 0)
        {
            result["hosts"] = args;
        }
        return result;
    }

    private Dictionary<string, object> ProcessRouteBlock(List<string> args, Dictionary<string, object> content)
    {
        var result = new Dictionary<string, object>(content);
        if (args.Count > 0)
        {
            result["match"] = new Dictionary<string, object> { ["path"] = args[0] };
        }
        return result;
    }

    private Dictionary<string, object> ProcessUpstreamBlock(List<string> args, Dictionary<string, object> content)
    {
        var result = new Dictionary<string, object>(content);
        if (args.Count > 0)
        {
            result["destinations"] = args.Select((addr, i) => new KeyValuePair<string, object>(
                $"dest{i + 1}",
                new Dictionary<string, object> { ["address"] = addr }
            )).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return result;
    }

    private static void MergeDict(Dictionary<string, object> target, Dictionary<string, object> source)
    {
        foreach (var kv in source)
        {
            if (target.TryGetValue(kv.Key, out var existing) && existing is Dictionary<string, object> existingDict
                && kv.Value is Dictionary<string, object> sourceDict)
            {
                MergeDict(existingDict, sourceDict);
            }
            else
            {
                target[kv.Key] = kv.Value;
            }
        }
    }

    #endregion
}

#region Token Types

public enum TokenType
{
    // 字面量
    Identifier,
    String,
    Number,
    Boolean,
    Variable,

    // 符号
    LeftBrace,      // {
    RightBrace,     // }
    LeftBracket,    // [
    RightBracket,   // ]
    Colon,          // :
    Comma,          // ,
    Equals,         // =
    EqualsEquals,   // ==
    NotEquals,      // !=
    NewLine,

    // 关键字
    If,
    Else,
    VarDecl,        // var, let
    Import,

    EOF
}

public record Token(TokenType Type, string Value, int Line);

public class LyConfigException : Exception
{
    public LyConfigException(string message) : base(message) { }
}

#endregion
