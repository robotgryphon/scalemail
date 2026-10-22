namespace ScaleMail.Tests;

internal static class Pgm
{
    public static (byte[] lum, int w, int h) Load(string path)
    {
        var d = File.ReadAllBytes(path);
        int pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)d[pos])) pos++;
            int s = pos;
            while (!char.IsWhiteSpace((char)d[pos])) pos++;
            return System.Text.Encoding.ASCII.GetString(d, s, pos - s);
        }
        Token(); int w = int.Parse(Token()), h = int.Parse(Token()); Token(); pos++;
        return (d[pos..], w, h);
    }
}
