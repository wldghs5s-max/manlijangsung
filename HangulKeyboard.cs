static class HangulKeyboard
{
    private static readonly string[] Choseong =
    [
        "r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g"
    ];

    private static readonly string[] Jungseong =
    [
        "k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l"
    ];

    private static readonly string[] Jongseong =
    [
        "", "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg",
        "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g"
    ];

    public static string ToDubeolsik(string text)
    {
        var keys = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (ch is < '\uAC00' or > '\uD7A3')
                continue;
            int code = ch - 0xAC00;
            keys.Append(Choseong[code / 588]);
            keys.Append(Jungseong[code % 588 / 28]);
            keys.Append(Jongseong[code % 28]);
        }
        return keys.ToString();
    }
}
