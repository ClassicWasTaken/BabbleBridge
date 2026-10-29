// Frame Bridge modification notice (2026-10-29): added the upstream ReinitializeRequested event and RequestReinitialize method from e71cbd086c7e9720634dda6b3e89318d43f6d9d1.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and repository NOTICE.txt.
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Library;

namespace VRCFaceTracking;

public abstract class ExtTrackingModule
{
    // Should UnifiedLibManager try to initialize this module if it's looking for a module that supports eye or lip.
    public virtual (bool SupportsEye, bool SupportsExpression) Supported => (false, false);

    public ModuleState Status = ModuleState.Uninitialized;

    public ILogger Logger;

    public ModuleMetadata ModuleInformation;

    // Modified by Babble Frame Bridge: forward-compatible reinitialization API from
    // VRCFaceTracking commit e71cbd086c7e9720634dda6b3e89318d43f6d9d1.
    // The bridge replaces its module worker when a runtime asks to change its claims.
    public event Action? ReinitializeRequested;

    protected void RequestReinitialize() => ReinitializeRequested?.Invoke();

    public abstract (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable);

    public abstract void Update();

    public abstract void Teardown();
}
