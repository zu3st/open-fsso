FROM jkroepke/wixtoolset@sha256:3a27ca1f1192e1f3e951814aa4da5fa4b6614d0eafebe7aca97cfcee8217144c
# WiX 5.0.2 and its Windows runtime run under Wine inside this Linux container.
# Product authoring also builds with native Windows WiX.
WORKDIR /packaging
COPY *.wxs build.sh ./
ENTRYPOINT ["bash", "/packaging/build.sh"]
