namespace TSC.Ares;

public static class GearSide
{
    public const int LeftForward = 1;
    public const int RightForward = 2;
    public const int LeftBackward = 3;
    public const int RightBackward = 4;
    public const int CenterLeft = 5;
    public const int CenterRight = 6;
    public const int Inactive = 7;
}

public sealed class GearState
{
    public int Left { get; set; } = GearSide.LeftBackward;
    public int Right { get; set; } = GearSide.RightBackward;
    public int CenterLeft { get; set; } = GearSide.Inactive;
    public int CenterRight { get; set; } = GearSide.Inactive;

    public GearState Copy() => new()
    {
        Left = Left,
        Right = Right,
        CenterLeft = CenterLeft,
        CenterRight = CenterRight
    };

    public string LeftMode => Left switch
    {
        GearSide.LeftForward => "1K",
        GearSide.LeftBackward => "3K",
        _ => "inactive"
    };

    public string RightMode => Right switch
    {
        GearSide.RightForward => "1K",
        GearSide.RightBackward => "3K",
        _ => "inactive"
    };

    public bool HasCenter => CenterLeft == GearSide.CenterLeft || CenterRight == GearSide.CenterRight;

    public string GlobalMode
    {
        get
        {
            if (HasCenter)
                return "2K";
            if (LeftMode == RightMode && (LeftMode == "1K" || LeftMode == "3K"))
                return LeftMode;
            return "mixed";
        }
    }

    public static GearState FromStartAttribute(int attr) => attr switch
    {
        0 or 2 => new GearState { Left = GearSide.LeftForward, Right = GearSide.RightForward },
        4 => new GearState { Left = GearSide.Inactive, Right = GearSide.Inactive, CenterLeft = GearSide.CenterLeft, CenterRight = GearSide.CenterRight },
        _ => new GearState()
    };

    /// <summary>
    /// Returns whether this event represents a valid SuperFlick path at this point
    /// in the route. `direction` is the raw attribute divided by ten:
    /// 2=Attr20 left outer 1K<->3K, 4=Attr40 right outer 1K<->3K,
    /// 6=Attr60: right 1K->2K, right 3K->2K, left 2K->1K;
    /// 7=Attr70: left 1K->2K, left 3K->2K, right 2K->1K.
    /// Attrs 80/90 are the distinct outer 2K->3K paths. For 60/70, the authored
    /// track bank identifies which path segment must be active.
    /// </summary>
    public bool CanApplySuperFlick(int direction, int track)
    {
        var position = Position(track);
        if (position is null)
            return false;
        var (side, bank, _) = position.Value;

        return direction switch
        {
            // Attr 20: outer left 1K <-> 3K. A 1K/3K bank cannot also
            // be active while the left 2K center bank is active.
            2 => side == "left" && CenterLeft != GearSide.CenterLeft &&
                 ((bank == "Forward" && Left == GearSide.LeftForward) ||
                  (bank == "Backward" && Left == GearSide.LeftBackward)),
            // Attr 40: outer right 1K <-> 3K.
            4 => side == "right" && CenterRight != GearSide.CenterRight &&
                 ((bank == "Forward" && Right == GearSide.RightForward) ||
                  (bank == "Backward" && Right == GearSide.RightBackward)),
            // Attr 60: right-side 1K->2K, right-side 3K->2K, left-side 2K->1K.
            6 => bank switch
            {
                "Forward" => side == "right" && Right == GearSide.RightForward && CenterRight != GearSide.CenterRight,
                "Backward" => side == "right" && Right == GearSide.RightBackward && CenterRight != GearSide.CenterRight,
                "Center" => side == "left" && CenterLeft == GearSide.CenterLeft && Left == GearSide.Inactive,
                _ => false
            },
            // Attr 70: left-side 1K->2K, left-side 3K->2K, right-side 2K->1K.
            7 => bank switch
            {
                "Forward" => side == "left" && Left == GearSide.LeftForward && CenterLeft != GearSide.CenterLeft,
                "Backward" => side == "left" && Left == GearSide.LeftBackward && CenterLeft != GearSide.CenterLeft,
                "Center" => side == "right" && CenterRight == GearSide.CenterRight && Right == GearSide.Inactive,
                _ => false
            },
            // Attr 80/90: outer 2K -> 3K, each only on its matching center bank.
            8 => side == "left" && bank == "Center" && CenterLeft == GearSide.CenterLeft && Left == GearSide.Inactive,
            9 => side == "right" && bank == "Center" && CenterRight == GearSide.CenterRight && Right == GearSide.Inactive,
            _ => false
        };
    }

    /// <summary>
    /// Applies the transition encoded by the SuperFlick path and its authored
    /// source bank. The track bank—not a guessed family or a previously active
    /// state flag—selects the 60/70 segment of the three-step path.
    /// </summary>
    public bool ApplySuperFlick(int direction, int track)
    {
        if (!CanApplySuperFlick(direction, track))
            return false;

        var position = Position(track);
        if (position is null)
            return false;

        var bank = position.Value.Bank;
        switch (direction)
        {
            case 2: // Attr 20: outer 1K <-> 3K, left bank.
                Left = bank == "Forward" ? GearSide.LeftBackward : GearSide.LeftForward;
                return true;
            case 4: // Attr 40: outer 1K <-> 3K, right bank.
                Right = bank == "Forward" ? GearSide.RightBackward : GearSide.RightForward;
                return true;
            case 6: // Attr 60: right 1K->2K, right 3K->2K, left 2K->1K.
                switch (bank)
                {
                    case "Forward":
                        Right = GearSide.Inactive;
                        CenterRight = GearSide.CenterRight;
                        return true;
                    case "Backward":
                        Right = GearSide.Inactive;
                        CenterRight = GearSide.CenterRight;
                        return true;
                    case "Center":
                        CenterLeft = GearSide.Inactive;
                        Left = GearSide.LeftForward;
                        return true;
                    default:
                        return false;
                }
            case 7: // Attr 70: left 1K->2K, left 3K->2K, right 2K->1K.
                switch (bank)
                {
                    case "Forward":
                        Left = GearSide.Inactive;
                        CenterLeft = GearSide.CenterLeft;
                        return true;
                    case "Backward":
                        Left = GearSide.Inactive;
                        CenterLeft = GearSide.CenterLeft;
                        return true;
                    case "Center":
                        CenterRight = GearSide.Inactive;
                        Right = GearSide.RightForward;
                        return true;
                    default:
                        return false;
                }
            case 8: // Attr 80: outer 2K -> 3K, left bank.
                CenterLeft = GearSide.Inactive;
                Left = GearSide.LeftBackward;
                return true;
            case 9: // Attr 90: outer 2K -> 3K, right bank.
                CenterRight = GearSide.Inactive;
                Right = GearSide.RightBackward;
                return true;
            default:
                return false;
        }
    }

    public bool IsActive(int track)
    {
        var side = NoteSide(track);
        return side == Left || side == Right || side == CenterLeft || side == CenterRight;
    }

    public static int NoteSide(int track) => track switch
    {
        >= 0 and <= 2 => GearSide.LeftForward,
        >= 3 and <= 5 => GearSide.RightForward,
        >= 6 and <= 8 => GearSide.LeftBackward,
        >= 9 and <= 11 => GearSide.RightBackward,
        12 or 13 => GearSide.CenterLeft,
        14 or 15 => GearSide.CenterRight,
        _ => GearSide.Inactive
    };

    public static (string Side, string Bank, int LocalPos)? Position(int track)
    {
        return track switch
        {
            0 => ("left", "Forward", 0),
            1 => ("left", "Forward", 1),
            2 => ("left", "Forward", 2),
            3 => ("right", "Forward", 0),
            4 => ("right", "Forward", 1),
            5 => ("right", "Forward", 2),
            6 => ("left", "Backward", 0),
            7 => ("left", "Backward", 1),
            8 => ("left", "Backward", 2),
            9 => ("right", "Backward", 0),
            10 => ("right", "Backward", 1),
            11 => ("right", "Backward", 2),
            12 => ("left", "Center", 0),
            13 => ("left", "Center", 1),
            14 => ("right", "Center", 0),
            15 => ("right", "Center", 1),
            _ => null
        };
    }
}
