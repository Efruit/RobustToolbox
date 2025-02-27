﻿using System;
using System.Collections.Generic;
using Robust.Shared.Log;
using Xilium.CefGlue;

namespace Robust.Client.CEF
{
    internal sealed class RobustRequestHandler : CefRequestHandler
    {
        private readonly ISawmill _sawmill;
        private readonly List<Action<RequestHandlerContext>> _resourceRequestHandlers = new();
        private readonly List<Action<BeforeBrowseContext>> _beforeBrowseHandlers = new();

        public RobustRequestHandler(ISawmill sawmill)
        {
            _sawmill = sawmill;
        }

        public void AddResourceRequestHandler(Action<RequestHandlerContext> handler)
        {
            lock (_resourceRequestHandlers)
            {
                _resourceRequestHandlers.Add(handler);
            }
        }

        public void RemoveResourceRequestHandler(Action<RequestHandlerContext> handler)
        {
            lock (_resourceRequestHandlers)
            {
                _resourceRequestHandlers.Remove(handler);
            }
        }

        public void AddBeforeBrowseHandler(Action<BeforeBrowseContext> handler)
        {
            lock (_beforeBrowseHandlers)
            {
                _beforeBrowseHandlers.Add(handler);
            }
        }

        public void RemoveBeforeBrowseHandler(Action<BeforeBrowseContext> handler)
        {
            lock (_beforeBrowseHandlers)
            {
                _beforeBrowseHandlers.Remove(handler);
            }
        }

        protected override CefResourceRequestHandler? GetResourceRequestHandler(
            CefBrowser browser,
            CefFrame frame,
            CefRequest request,
            bool isNavigation,
            bool isDownload,
            string requestInitiator,
            ref bool disableDefaultHandling)
        {
            lock (_resourceRequestHandlers)
            {
                _sawmill.Debug($"HANDLING REQUEST: {request.Url}");

                var context = new RequestHandlerContext(isNavigation, isDownload, requestInitiator, request);

                foreach (var handler in _resourceRequestHandlers)
                {
                    handler(context);

                    if (context.IsHandled)
                        disableDefaultHandling = true;

                    if (context.IsCancelled)
                        return null;

                    if (context.Result != null)
                        return new WrapReaderResourceHandler(context.Result.MakeHandler());
                }
            }

            return null;
        }

        protected override bool OnBeforeBrowse(CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
        {
            lock (_beforeBrowseHandlers)
            {
                var context = new BeforeBrowseContext(isRedirect, userGesture, request);

                foreach (var handler in _beforeBrowseHandlers)
                {
                    handler(context);

                    if (context.IsCancelled)
                        return true;
                }
            }

            return false;
        }

        private sealed class WrapReaderResourceHandler : CefResourceRequestHandler
        {
            private readonly CefResourceHandler _handler;

            public WrapReaderResourceHandler(CefResourceHandler handler)
            {
                _handler = handler;
            }

            protected override CefCookieAccessFilter? GetCookieAccessFilter(
                CefBrowser browser,
                CefFrame frame,
                CefRequest request)
            {
                return null;
            }

            protected override CefResourceHandler GetResourceHandler(
                CefBrowser browser,
                CefFrame frame,
                CefRequest request)
            {
                return _handler;
            }
        }
    }
}
