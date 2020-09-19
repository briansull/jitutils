#!/usr/bin/env bash

RootDirectory="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
SourcesDirectory=$RootDirectory/src
BinariesDirectory=$RootDirectory/obj
TargetOSArchitecture=$1
CrossRootfsDirectory=$2

case "$TargetOSArchitecture" in
    linux-arm)
        CrossCompiling=1
        TargetTriple=arm-linux-gnueabihf
        ;;

    linux-arm64)
        CrossCompiling=1
        TargetTriple=aarch64-linux-gnu
        ;;

    linux-x64|macos-x64)
        CrossCompiling=0
        ;;

    *)
        echo "Unknown target OS and architecture: $TargetOSArchitecture"
        exit 1
esac

if [[ $CrossCompiling -eq 1 && ! -d $CrossRootfsDirectory ]]; then
    echo "Invalid or unspecified CrossRootfsDirectory: $CrossRootfsDirectory"
    exit 1
fi

if [ ! -d $BinariesDirectory ]; then
    mkdir -p $BinariesDirectory
fi

pushd "$BinariesDirectory"

if [ "$CrossCompiling" -eq 1 ]; then
    cmake \
        -G "Unix Makefiles" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_CROSSCOMPILING=ON \
        -DCMAKE_C_COMPILER=$(which clang) \
        -DCMAKE_C_FLAGS="-target $TargetTriple --sysroot=$CrossRootfsDirectory" \
        -DCMAKE_CXX_COMPILER=$(which clang++) \
        -DCMAKE_CXX_FLAGS="-target $TargetTriple --sysroot=$CrossRootfsDirectory" \
        -DCMAKE_INCLUDE_PATH=$CrossRootfsDirectory/usr/include \
        -DCMAKE_INSTALL_PREFIX=$RootDirectory \
        -DCMAKE_LIBRARY_PATH=$CrossRootfsDirectory/usr/lib/$TargetTriple \
        -DLLVM_EXTERNAL_PROJECTS=coredistools \
        -DLLVM_EXTERNAL_COREDISTOOLS_SOURCE_DIR=$SourcesDirectory/coredistools \
        -DLLVM_HOST_TRIPLE=$TargetTriple \
        -DLLVM_TABLEGEN=$(which llvm-tblgen) \
        -DLLVM_TARGETS_TO_BUILD="AArch64;ARM;X86" \
        -DLLVM_TOOL_COREDISTOOLS_BUILD=ON \
        $SourcesDirectory/llvm-project/llvm
else
    cmake \
        -G "Unix Makefiles" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_C_COMPILER=$(which clang) \
        -DCMAKE_CXX_COMPILER=$(which clang++) \
        -DCMAKE_INSTALL_PREFIX=$RootDirectory \
        -DLLVM_EXTERNAL_PROJECTS=coredistools \
        -DLLVM_EXTERNAL_COREDISTOOLS_SOURCE_DIR=$SourcesDirectory/coredistools \
        -DLLVM_TABLEGEN=$(which llvm-tblgen) \
        -DLLVM_TARGETS_TO_BUILD="AArch64;ARM;X86" \
        -DLLVM_TOOL_COREDISTOOLS_BUILD=ON \
        $SourcesDirectory/llvm-project/llvm
fi

popd

cmake \
    --build $BinariesDirectory \
    --target coredistools

if [ "$?" -ne 0 ]; then
    echo "coredistools compilation has failed"
    exit 1
fi

cmake \
    --install $BinariesDirectory \
    --component coredistools