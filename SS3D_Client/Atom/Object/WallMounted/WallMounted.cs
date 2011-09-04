﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;

namespace SS3D.Atom.Object.WallMounted
{
    public abstract class WallMounted : Object
    {
        public WallMounted()
            : base()
        {
            spritename = "worktop";
            collidable = false;
            snapTogrid = true;
        }
    }
}
