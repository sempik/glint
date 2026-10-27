namespace Glint.Services;

/// <summary>Quick sanity check for <see cref="TextTransforms"/>; meant to be called from a debugger or test harness, not at runtime.</summary>
internal static class TextTransformsSelfTest
{
    internal static List<string> Run()
    {
        var failures = new List<string>();

        void Eq(string name, string expected, Func<string> actual)
        {
            try
            {
                var got = actual();
                if (got != expected) failures.Add($"{name}: ожидалось «{expected}», получено «{got}»");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: исключение {ex.GetType().Name}: {ex.Message}");
            }
        }

        void Throws<T>(string name, Action action) where T : Exception
        {
            try
            {
                action();
                failures.Add($"{name}: ожидалось исключение {typeof(T).Name}");
            }
            catch (T)
            {
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: неожиданное исключение {ex.GetType().Name}");
            }
        }

        Eq("layout en→ru", "привет как дела", () => TextTransforms.SwitchLayout("ghbdtn rfr ltkf"));
        Eq("layout case", "Привет", () => TextTransforms.SwitchLayout("Ghbdtn"));
        Eq("layout ru→en", "hello", () => TextTransforms.SwitchLayout("руддщ"));
        Eq("layout punctuation en→ru", "хъжэбю.", () => TextTransforms.SwitchLayout("[];',./"));
        Eq("layout shifted ru→en", "Lfdfq? Ns ult@", () => TextTransforms.SwitchLayout("Давай, Ты где\""));
        Eq("layout roundtrip", "Съешь же ещё этих мягких булок", () =>
            TextTransforms.SwitchLayout(TextTransforms.SwitchLayout("Съешь же ещё этих мягких булок")));

        Eq("translit", "Shchuka i yozh", () => TextTransforms.Transliterate("Щука и ёж"));
        Eq("translit caps", "SHCHUKA", () => TextTransforms.Transliterate("ЩУКА"));
        Eq("translit signs", "obekt", () => TextTransforms.Transliterate("объект"));

        Eq("title", "Каждое Слово Here", () => TextTransforms.TitleCase("кАЖДОЕ слово here"));
        Eq("sentence", "Первое. Второе! Третье 3.5 тут", () => TextTransforms.SentenceCase("пЕРВОЕ. второе! ТРЕТЬЕ 3.5 тут"));
        Eq("collapse", "a b\nc d", () => TextTransforms.CollapseSpaces("  a \t  b \n  c   d  "));
        Eq("linebreaks", "one two three", () => TextTransforms.RemoveLineBreaks("one\r\ntwo  \n\n  three\n"));

        Eq("base64 roundtrip", "Привет, мир!", () => TextTransforms.FromBase64(TextTransforms.ToBase64("Привет, мир!")));
        Eq("base64 url-safe no padding", "Привет!", () => TextTransforms.FromBase64("0J_RgNC4\n0LLQtdGCIQ"));
        Throws<FormatException>("base64 invalid", () => TextTransforms.FromBase64("@@@"));

        Eq("json minify", "{\"a\":[1,2],\"b\":\"тест\"}", () => TextTransforms.JsonMinify("{ \"a\": [1, 2],\n \"b\": \"тест\" }"));
        Eq("json pretty", "{\n  \"a\": 1\n}", () => TextTransforms.JsonPretty("{\"a\":1}").Replace("\r\n", "\n"));
        Throws<FormatException>("json invalid", () => TextTransforms.JsonPretty("{a:1"));

        Eq("url roundtrip", "a b&c=д", () => TextTransforms.UrlDecode(TextTransforms.UrlEncode("a b&c=д")));

        if (TextTransforms.CountWords("раз, два — три") != 3) failures.Add("words: ожидалось 3");
        if (TextTransforms.CountLines("a\r\nb\nc") != 3) failures.Add("lines: ожидалось 3");
        if (TextTransforms.CountChars("a\r\nb") != 3) failures.Add("chars: ожидалось 3");

        return failures;
    }
}
