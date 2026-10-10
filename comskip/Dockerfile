# Self-contained (static) Comskip for Linux, built from https://github.com/erikkaashoek/Comskip (GPL-2.0)
# with a small ffmpeg (only what Comskip reads: TV recordings and common video files) and argtable2.
FROM alpine:3.20 AS build
ARG FFMPEG_VERSION=6.1.2
ARG COMSKIP_REF=master
RUN apk add --no-cache build-base autoconf automake libtool pkgconf git nasm wget tar xz linux-headers
WORKDIR /src
RUN wget -q https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz && tar xf ffmpeg-${FFMPEG_VERSION}.tar.xz \
 && cd ffmpeg-${FFMPEG_VERSION} && ./configure --prefix=/usr/local --enable-static --disable-shared \
    --disable-programs --disable-doc --disable-network --disable-autodetect --disable-everything \
    --enable-protocol=file,pipe \
    --enable-demuxer=mpegts,mpegps,mpegvideo,matroska,mov,h264,hevc,avi,wtv,flv \
    --enable-decoder=mpeg2video,mpeg1video,h264,hevc,vc1,mpeg4,ac3,eac3,aac,aac_latm,mp2,mp3 \
    --enable-parser=mpegvideo,h264,hevc,vc1,mpeg4video,ac3,aac,aac_latm,mpegaudio \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb \
    --enable-swscale --enable-swresample --enable-pic \
 && make -j"$(nproc)" && make install
RUN wget -q -O argtable2-13.tar.gz https://downloads.sourceforge.net/project/argtable/argtable/argtable-2.13/argtable2-13.tar.gz \
 && tar xzf argtable2-13.tar.gz && cd argtable2-13 && ./configure --prefix=/usr/local --disable-shared --enable-static && make -j"$(nproc)" && make install
RUN git clone https://github.com/erikkaashoek/Comskip && cd Comskip && git checkout ${COMSKIP_REF} \
 && ./autogen.sh && PKG_CONFIG_PATH=/usr/local/lib/pkgconfig ./configure --enable-static --disable-gui \
 && make -j"$(nproc)" && strip comskip && cp comskip /comskip && git rev-parse --short HEAD > /comskip.commit

FROM scratch
COPY --from=build /comskip /comskip.commit /
