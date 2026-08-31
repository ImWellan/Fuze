# Fuse Player V1.0.0 - release checklist

This checklist is packaging guidance, not legal advice.

## Completed in this workspace

- [x] Record the exact mpv/libmpv and FFmpeg revisions used by
      `Native/libmpv-2.dll`.
- [x] Record the `mpv-winbuild-cmake` tag, target architecture and native DLL
      SHA-256.
- [x] Preserve the matching FFmpeg configure result (`GPL version 3 or
      later`) and the enabled-library inventory.
- [x] Include the Fuse source, native source trees, build recipes, local
      patches and original third-party license files in the Code package.
- [x] Include the core GPL/LGPL, OpenSSL, flag-icons and .NET notices in
      `Licences Open`.
- [x] Keep the runtime package's notice, copyright, source and build records
      synchronized with the Code package.
- [x] Keep the runtime and source documentation consistent with the native
      build provenance.

## Still required for a public release

- [ ] Publish `Fuse Player Code V1.0.0` at a stable public URL (for example a
      GitHub release or tagged source archive) and keep it available while the
      binary is distributed.
- [ ] Publish the matching `Fuse Player V1.0.0` runtime package alongside it.
- [ ] Do not replace the current native DLL with one built from another mpv or
      FFmpeg revision without updating every build and notice document.
- [ ] If the two historical source archives are removed or replaced, update
      `SOURCE-CODE.md` and this checklist accordingly.
- [ ] Recompute `SHA256SUMS.txt` and any release archives after changing the
      package contents.
- [ ] Keep any project-specific terms compatible with the GPL rights. Do not
      add a no-reverse-engineering restriction or an EULA that conflicts with
      those rights.

## Optional written-offer route

If the corresponding source is not shipped beside the binary, provide a
written offer valid for at least three years that identifies the exact binary,
explains how to request the complete source and gives a reliable delivery
method. Publishing the matching Code package is simpler.
