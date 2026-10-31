# Avatar route recovery

Run `dotnet run --project tests/Avatar.Router.Tests -c Release`.

A local HTTP OSCQuery fixture and random loopback UDP ports exercise native
route continuity after a transient server failure, explicit capability changes,
avatar-change resets and successful query recovery. No tracking module, native
OSC encoder, mDNS advertisement, running VRChat session or user profile is used.
