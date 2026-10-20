namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kmovement.cpp ----

    /// <summary>Signal bits of view objects. Mirrors ViewSignals in animate.h.</summary>
    public const int SignalHitObstacle = 0x0400;
    public const int SignalDoesntTurn = 0x0800;

    /// <summary>
    /// Computes the step vector of a jump from (0,0) to (dx,dy) under gravity gy so the
    /// discrete motion looks like a parabola. Port of kSetJump with its approximations.
    /// </summary>
    public static RegT SetJump(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        int dx = argv.Int16(1);
        int dy = argv.Int16(2);
        int gy = argv.Int16(3);

        int c;
        int vx = 0;
        int vy;
        bool dxWasNegative = dx < 0;
        dx = Math.Abs(dx);

        if (dx == 0)
        {
            c = 1; // an upward jump, c does not matter
        }
        else
        {
            // keep the discrete velocities small for a smooth curve
            if (dx + dy < 0)
            {
                c = 2 * Math.Abs(dy) / dx; // dy negative and |dy| > |dx|
            }
            else
            {
                c = (dx * 3 / 2 - dy) / dx;
                if (c < 1)
                {
                    c = 1;
                }
            }
        }

        int tmp = c * dx + dy;
        if (tmp != 0 && dx != 0)
        {
            vx = (short)(float)(dx * Math.Sqrt(gy / (2.0 * tmp)));
        }

        if (dxWasNegative)
        {
            vx = -vx;
        }

        if (dy < 0 && vx == 0)
        {
            // a jump (almost) straight up: compute vy directly
            vy = (int)Math.Sqrt((float)gy * Math.Abs(2 * dy)) + 1;
        }
        else
        {
            vy = c * vx;
        }

        vy = -Math.Abs(vy); // always upwards

        Selectors.WriteSelectorValue(s, obj, sel.XStep, vx);
        Selectors.WriteSelectorValue(s, obj, sel.YStep, vy);
        return s.Acc;
    }

    public static RegT InitBresen(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var mover = argv[0];
        var client = Selectors.ReadSelector(s, mover, sel.Client);
        int stepFactor = argv.Count >= 2 ? argv.UInt16(1) : 1;
        short moverX = (short)Selectors.ReadSelectorValue(s, mover, sel.X);
        short moverY = (short)Selectors.ReadSelectorValue(s, mover, sel.Y);
        short clientXStep = (short)((short)Selectors.ReadSelectorValue(s, client, sel.XStep) * stepFactor);
        short clientYStep = (short)((short)Selectors.ReadSelectorValue(s, client, sel.YStep) * stepFactor);

        short clientStep = clientXStep < clientYStep ? (short)(clientYStep * 2) : (short)(clientXStep * 2);

        short deltaX = (short)(moverX - (short)Selectors.ReadSelectorValue(s, client, sel.X));
        short deltaY = (short)(moverY - (short)Selectors.ReadSelectorValue(s, client, sel.Y));
        short moverDx;
        short moverDy;
        short moverI1;
        short moverI2;
        short moverDi;
        short moverIncr;
        short moverXAxis;

        while (true)
        {
            moverDx = clientXStep;
            moverDy = clientYStep;
            moverIncr = 1;

            if (Math.Abs(deltaX) >= Math.Abs(deltaY))
            {
                moverXAxis = 1;
                if (deltaX < 0)
                {
                    moverDx = (short)-moverDx;
                }

                moverDy = (short)(deltaX != 0 ? moverDx * deltaY / deltaX : 0);
                moverI1 = (short)(((moverDx * deltaY) - (moverDy * deltaX)) * 2);
                if (deltaY < 0)
                {
                    moverIncr = -1;
                    moverI1 = (short)-moverI1;
                }

                moverI2 = (short)(moverI1 - (deltaX * 2));
                moverDi = (short)(moverI1 - deltaX);
                if (deltaX < 0)
                {
                    moverI1 = (short)-moverI1;
                    moverI2 = (short)-moverI2;
                    moverDi = (short)-moverDi;
                }
            }
            else
            {
                moverXAxis = 0;
                if (deltaY < 0)
                {
                    moverDy = (short)-moverDy;
                }

                moverDx = (short)(deltaY != 0 ? moverDy * deltaX / deltaY : 0);
                moverI1 = (short)(((moverDy * deltaX) - (moverDx * deltaY)) * 2);
                if (deltaX < 0)
                {
                    moverIncr = -1;
                    moverI1 = (short)-moverI1;
                }

                moverI2 = (short)(moverI1 - (deltaY * 2));
                moverDi = (short)(moverI1 - deltaY);
                if (deltaY < 0)
                {
                    moverI1 = (short)-moverI1;
                    moverI2 = (short)-moverI2;
                    moverDi = (short)-moverDi;
                }

                break;
            }

            if (clientXStep <= clientYStep)
            {
                break;
            }

            if (clientXStep == 0)
            {
                break;
            }

            if (clientYStep >= Math.Abs(moverDy + moverIncr))
            {
                break;
            }

            clientStep--;
            if (clientStep == 0)
            {
                throw new SciVmException("kInitBresen failed");
            }

            clientXStep--;
        }

        Selectors.WriteSelectorValue(s, mover, sel.Dx, moverDx);
        Selectors.WriteSelectorValue(s, mover, sel.Dy, moverDy);
        Selectors.WriteSelectorValue(s, mover, sel.BI1, moverI1);
        Selectors.WriteSelectorValue(s, mover, sel.BI2, moverI2);
        Selectors.WriteSelectorValue(s, mover, sel.BDi, moverDi);
        Selectors.WriteSelectorValue(s, mover, sel.BIncr, moverIncr);
        Selectors.WriteSelectorValue(s, mover, sel.BXAxis, moverXAxis);
        return s.Acc;
    }

    public static async ValueTask<RegT> DoBresenAsync(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var mover = argv[0];
        var client = Selectors.ReadSelector(s, mover, sel.Client);
        bool completed = false;
        bool handleMoveCount = s.Features.HandleMoveCount;

        if (s.Version >= SciVersion.V1EgaOnly)
        {
            int clientSignal = Selectors.ReadSelectorValue(s, client, sel.Signal);
            Selectors.WriteSelectorValue(s, client, sel.Signal, clientSignal & ~SignalHitObstacle);
        }

        short moverMoveCnt = 1;
        short clientMoveSpeed = 0;
        if (handleMoveCount)
        {
            moverMoveCnt = (short)Selectors.ReadSelectorValue(s, mover, sel.BMovCnt);
            clientMoveSpeed = (short)Selectors.ReadSelectorValue(s, client, sel.MoveSpeed);
            moverMoveCnt++;
        }

        if (clientMoveSpeed < moverMoveCnt)
        {
            moverMoveCnt = 0;
            short clientX = (short)Selectors.ReadSelectorValue(s, client, sel.X);
            short clientY = (short)Selectors.ReadSelectorValue(s, client, sel.Y);
            short moverX = (short)Selectors.ReadSelectorValue(s, mover, sel.X);
            short moverY = (short)Selectors.ReadSelectorValue(s, mover, sel.Y);
            short moverXAxis = (short)Selectors.ReadSelectorValue(s, mover, sel.BXAxis);
            short moverDx = (short)Selectors.ReadSelectorValue(s, mover, sel.Dx);
            short moverDy = (short)Selectors.ReadSelectorValue(s, mover, sel.Dy);
            short moverIncr = (short)Selectors.ReadSelectorValue(s, mover, sel.BIncr);
            short moverI1 = (short)Selectors.ReadSelectorValue(s, mover, sel.BI1);
            short moverI2 = (short)Selectors.ReadSelectorValue(s, mover, sel.BI2);
            short moverDi = (short)Selectors.ReadSelectorValue(s, mover, sel.BDi);
            short moverOrgI1 = moverI1;
            short moverOrgI2 = moverI2;
            short moverOrgDi = moverDi;

            if (s.Version >= SciVersion.V1EgaOnly)
            {
                // the current position is saved into the mover
                Selectors.WriteSelectorValue(s, mover, sel.XLast, clientX);
                Selectors.WriteSelectorValue(s, mover, sel.YLast, clientY);
            }

            // the client's variables are backed up and restored on a collision
            var clientObject = s.SegMan.GetObject(client) ?? throw new SciVmException($"kDoBresen: client {client} is not an object");
            var clientBackup = new RegT[clientObject.VarCount];
            for (int i = 0; i < clientBackup.Length; i++)
            {
                clientBackup[i] = clientObject.GetVariable(i);
            }

            if (s.Version <= SciVersion.V1EgaOnly)
            {
                if (moverXAxis != 0 ? Math.Abs(moverX - clientX) < Math.Abs(moverDx) : Math.Abs(moverY - clientY) < Math.Abs(moverDy))
                {
                    completed = true;
                }
            }
            else
            {
                if (moverXAxis != 0 ? Math.Abs(moverX - clientX) <= Math.Abs(moverDx) : Math.Abs(moverY - clientY) <= Math.Abs(moverDy))
                {
                    completed = true;
                }
            }

            if (completed)
            {
                clientX = moverX;
                clientY = moverY;
            }
            else
            {
                clientX += moverDx;
                clientY += moverDy;
                if (moverDi < 0)
                {
                    moverDi += moverI1;
                }
                else
                {
                    moverDi += moverI2;
                    if (moverXAxis == 0)
                    {
                        clientX += moverIncr;
                    }
                    else
                    {
                        clientY += moverIncr;
                    }
                }
            }

            Selectors.WriteSelectorValue(s, client, sel.X, clientX);
            Selectors.WriteSelectorValue(s, client, sel.Y, clientY);

            // client::canBeHere or client::cantBeHere checks for collisions; the result is
            // reset first because Hoyle 3's dummy CantBeHere leaves acc set
            bool collision = false;
            s.Acc = RegT.Null;
            if (sel.CantBeHere != -1)
            {
                await Selectors.InvokeSelector(s, client, sel.CantBeHere, argv);
                if (!s.Acc.IsNull)
                {
                    collision = true;
                }
            }
            else
            {
                await Selectors.InvokeSelector(s, client, sel.CanBeHere, argv);
                if (s.Acc.IsNull)
                {
                    collision = true;
                }
            }

            if (collision)
            {
                for (int i = 0; i < clientBackup.Length; i++)
                {
                    clientObject.SetVariable(i, clientBackup[i]);
                }

                moverI1 = moverOrgI1;
                moverI2 = moverOrgI2;
                moverDi = moverOrgDi;

                int clientSignal = Selectors.ReadSelectorValue(s, client, sel.Signal);
                Selectors.WriteSelectorValue(s, client, sel.Signal, clientSignal | SignalHitObstacle);
            }

            Selectors.WriteSelectorValue(s, mover, sel.BI1, moverI1);
            Selectors.WriteSelectorValue(s, mover, sel.BI2, moverI2);
            Selectors.WriteSelectorValue(s, mover, sel.BDi, moverDi);

            if (s.Version >= SciVersion.V1EgaOnly)
            {
                if (handleMoveCount)
                {
                    Selectors.WriteSelectorValue(s, mover, sel.BMovCnt, moverMoveCnt);
                }

                // completion may have happened during this move
                if (clientX == moverX && clientY == moverY)
                {
                    await Selectors.InvokeSelector(s, mover, sel.MoveDone, argv);
                }

                return s.Acc;
            }
        }

        if (handleMoveCount)
        {
            Selectors.WriteSelectorValue(s, mover, sel.BMovCnt, moverMoveCnt);
        }

        return s.Acc;
    }

    public static async ValueTask<RegT> DoAvoiderAsync(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var avoider = argv[0];
        int timesStep = argv.Count > 1 ? argv.UInt16(1) : 1;

        // the avoider may already have been freed by a previous mover:doit (KQ4 early room 57)
        if (!s.SegMan.IsObject(avoider))
        {
            throw new SciVmException($"DoAvoider() where avoider {avoider} is not an object");
        }

        var client = Selectors.ReadSelector(s, avoider, sel.Client);
        var mover = Selectors.ReadSelector(s, client, sel.Mover);
        if (mover.IsNull)
        {
            return RegT.Signal;
        }

        await Selectors.InvokeSelector(s, mover, sel.Doit, argv);

        mover = Selectors.ReadSelector(s, client, sel.Mover);
        if (mover.IsNull)
        {
            return RegT.Signal;
        }

        short clientX = (short)Selectors.ReadSelectorValue(s, client, sel.X);
        short clientY = (short)Selectors.ReadSelectorValue(s, client, sel.Y);
        short moverX = (short)Selectors.ReadSelectorValue(s, mover, sel.X);
        short moverY = (short)Selectors.ReadSelectorValue(s, mover, sel.Y);
        short avoiderHeading = (short)Selectors.ReadSelectorValue(s, avoider, sel.Heading);

        await Selectors.InvokeSelector(s, client, sel.IsBlocked, argv);

        if (s.Acc.IsNull)
        {
            // not blocked
            if (avoiderHeading == -1)
            {
                return RegT.Signal;
            }

            avoiderHeading = -1;
            ushort angle = GetAngleWorker(s, clientX, clientY, moverX, moverY);
            var clientLooper = Selectors.ReadSelector(s, client, sel.Looper);
            if (clientLooper.IsNull)
            {
                DirLoopWorker(s, client, angle);
            }
            else
            {
                await Selectors.InvokeSelector(s, clientLooper, sel.Doit, argv, RegT.Make(0, angle), client);
            }

            s.Acc = RegT.Signal;
        }
        else
        {
            // blocked
            if (avoiderHeading == -1)
            {
                avoiderHeading = (short)(s.Rng.Next(2) != 0 ? 45 : -45);
            }

            short clientHeading = (short)Selectors.ReadSelectorValue(s, client, sel.Heading);
            clientHeading = (short)(clientHeading / 45 * 45);

            short clientXstep = (short)((short)Selectors.ReadSelectorValue(s, client, sel.XStep) * timesStep);
            short clientYstep = (short)((short)Selectors.ReadSelectorValue(s, client, sel.YStep) * timesStep);
            short newHeading = clientHeading;

            while (true)
            {
                short newX = clientX;
                short newY = clientY;
                switch (newHeading)
                {
                    case 45:
                    case 90:
                    case 135:
                        newX += clientXstep;
                        break;
                    case 225:
                    case 270:
                    case 315:
                        newX -= clientXstep;
                        break;
                }

                switch (newHeading)
                {
                    case 0:
                    case 45:
                    case 315:
                        newY -= clientYstep;
                        break;
                    case 135:
                    case 180:
                    case 225:
                        newY += clientYstep;
                        break;
                }

                Selectors.WriteSelectorValue(s, client, sel.X, newX);
                Selectors.WriteSelectorValue(s, client, sel.Y, newY);

                await Selectors.InvokeSelector(s, client, sel.CanBeHere, argv);
                if (!s.Acc.IsNull)
                {
                    s.Acc = RegT.Make(0, newHeading);
                    break;
                }

                newHeading += avoiderHeading;
                if (newHeading >= 360)
                {
                    newHeading -= 360;
                }

                if (newHeading < 0)
                {
                    newHeading += 360;
                }

                if (newHeading == clientHeading)
                {
                    // everything was tried
                    Selectors.WriteSelectorValue(s, client, sel.X, clientX);
                    Selectors.WriteSelectorValue(s, client, sel.Y, clientY);
                    s.Acc = RegT.Signal;
                    break;
                }
            }
        }

        Selectors.WriteSelectorValue(s, avoider, sel.Heading, avoiderHeading);
        return s.Acc;
    }
}
