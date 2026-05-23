class LabelEntry
{
    public string Name;
    public int AnchorX, AnchorY;
    public int X, Y;
    public int W, H;
    public string Reason = "";
    public double Confidence = 1.0;

    public LabelEntry(string name, int ax, int ay, int w, int h)
    {
        Name = name; AnchorX = ax; AnchorY = ay; X = ax - w / 2; Y = ay - h / 2; W = w; H = h;
    }
}
