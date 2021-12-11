﻿using System;

namespace Robust.Client.Console
{
    public interface IClientConGroupImplementation
    {
        bool CanCommand(string cmdName);
        bool CanViewVar(bool write);
        bool CanAdminPlace();
        bool CanScript();
        bool CanAdminMenu();

        event Action ConGroupUpdated;
    }
}
