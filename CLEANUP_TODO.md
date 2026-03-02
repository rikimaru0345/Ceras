# Ceras API Cleanup Tasks

- [ ] 1. Make `CerasSerializer` internal and remove `ICerasAdvanced`.
- [ ] 2. Remove `ExternalObjectResolver`, `OnExternalObject`, `KnownTypes`, and `DiscardObjectMethod`.
- [ ] 3. Fix `NormalizedTypeSchema` logic duplication in `CerasRobustSerializer`.
- [ ] 4. Replace Header offset constants in `CerasRobustSerializer` with a `Header` struct.
- [ ] 5. Remove `[Monomorphic]` and `[Polymorphic]` attributes.
- [ ] 6. Enforce "sealed" check on all config properties in `CerasConfig` and `TypeConfig`.
- [ ] 7. Remove `TargetMember` enum and related logic.
- [ ] 8. Remove `CerasSerializableAttribute.DiscoverSubtypes`.
- [ ] 9. Remove `Serialize` and `Deserialize` methods from `CerasRobustSerializer`.
- [ ] 10. Remove `EmbedChecksum` property from `CerasConfig` but expose `ProtocolChecksum` where appropriate.
