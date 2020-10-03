#define mgGIF_UNSAFE

using UnityEngine;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MG.GIF
{
    ////////////////////////////////////////////////////////////////////////////////

    public class Image : ICloneable
    {
        public int       Width;
        public int       Height;
        public int       Delay; // milliseconds
        public Color32[] RawImage;

        public Image()
        {
        }

        public Image( Image img )
        {
            Width    = img.Width;
            Height   = img.Height;
            Delay    = img.Delay;
            RawImage = img.RawImage != null ? (Color32[]) img.RawImage.Clone() : null;
        }

        public object Clone()
        {
            return new Image( this );
        }

        public Texture2D CreateTexture()
        {
            var tex = new Texture2D( Width, Height, TextureFormat.ARGB32, false )
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };

            tex.SetPixels32( RawImage );
            tex.Apply();

            return tex;
        }
    }

    ////////////////////////////////////////////////////////////////////////////////

#if mgGIF_UNSAFE
    unsafe
#endif
    public class Decoder : IDisposable
    {
        public string  Version;
        public ushort  Width;
        public ushort  Height;
        public Color32 BackgroundColour;


        //------------------------------------------------------------------------------

        [Flags]
        private enum ImageFlag
        {
            Interlaced        = 0x40,
            ColourTable       = 0x80,
            TableSizeMask     = 0x07,
            BitDepthMask      = 0x70,
        }

        private enum Block
        {
            Image             = 0x2C,
            Extension         = 0x21,
            End               = 0x3B
        }

        private enum Extension
        {
            GraphicControl    = 0xF9,
            Comments          = 0xFE,
            PlainText         = 0x01,
            ApplicationData   = 0xFF
        }

        private enum Disposal
        {
            None              = 0x00,
            DoNotDispose      = 0x04,
            RestoreBackground = 0x08,
            ReturnToPrevious  = 0x0C
        }

        const uint          NoCode         = 0xFFFF;
        const ushort        NoTransparency = 0xFFFF;

        // input stream to decode
        byte[]              Input;
        int                 D;

        // colour table
        private Color32[]   GlobalColourTable;
        private Color32[]   LocalColourTable;
        private Color32[]   ActiveColourTable;
        private ushort      TransparentIndex;

        // current controls
        private ushort      ControlDelay;

        // current image
        private ushort      ImageLeft;
        private ushort      ImageTop;
        private ushort      ImageWidth;
        private ushort      ImageHeight;

        private Color32[]   Output;
        private Color32[]   PreviousImage;

        readonly int[]      Pow2 = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };

        //------------------------------------------------------------------------------

        public Decoder( byte[] data )
            : this()
        {
            Load( data );
        }

        // load data

        public Decoder Load( byte[] data )
        {
            Input             = data;
            D                 = 0;

            GlobalColourTable = new Color32[ 256 ];
            LocalColourTable  = new Color32[ 256 ];
            TransparentIndex  = NoTransparency;
            ControlDelay      = 0;
            Output            = null;
            PreviousImage     = null;

            return this;
        }


        //------------------------------------------------------------------------------
        // reading data utility functions

        [MethodImpl( MethodImplOptions.AggressiveInlining )]
        byte ReadByte()
        {
            return Input[ D++ ];
        }

        [MethodImpl( MethodImplOptions.AggressiveInlining )]
        ushort ReadUInt16()
        {
            return (ushort) ( Input[ D++ ] | Input[ D++ ] << 8 );
        }

        //------------------------------------------------------------------------------

        private Color32[] ReadColourTable( Color32[] colourTable, ImageFlag flags )
        {
            var tableSize = Pow2[ (int)( flags & ImageFlag.TableSizeMask ) + 1 ];

            for( var i = 0; i < tableSize; i++ )
            {
                colourTable[ i ] = new Color32(
                    Input[ D++ ],
                    Input[ D++ ],
                    Input[ D++ ],
                    0xFF
                );
            }

            return colourTable;
        }

        //------------------------------------------------------------------------------

        protected void ReadHeader()
        {
            if( Input == null || Input.Length <= 12 )
            {
                throw new Exception( "Invalid data" );
            }

            // signature

            Version = new string( new char[] {
                (char) Input[ 0 ],
                (char) Input[ 1 ],
                (char) Input[ 2 ],
                (char) Input[ 3 ],
                (char) Input[ 4 ],
                (char) Input[ 5 ]
            } );

            D = 6;

            if( Version != "GIF87a" && Version != "GIF89a" )
            {
                throw new Exception( "Unsupported GIF version" );
            }

            // read header

            Width  = ReadUInt16();
            Height = ReadUInt16();

            var flags   = (ImageFlag) ReadByte();
            var bgIndex = ReadByte(); // background colour

            ReadByte(); // aspect ratio

            if( flags.HasFlag( ImageFlag.ColourTable ) )
            {
                ReadColourTable( GlobalColourTable, flags );
            }

            BackgroundColour = GlobalColourTable[ bgIndex ];
        }

        //------------------------------------------------------------------------------

        public Image NextImage()
        {
            // if at start of data, read header

            if( D == 0 )
            {
                ReadHeader();
            }

            // read blocks until we find an image block

            while( true )
            {
                var block = (Block) ReadByte();

                switch( block )
                {
                    case Block.Image:

                        // return the image if we got one

                        var img = ReadImageBlock();

                        if( img != null )
                        {
                            return img;
                        }
                        break;

                    case Block.Extension:

                        var ext = (Extension) ReadByte();

                        switch( ext )
                        {
                            case Extension.GraphicControl:
                                ReadControlBlock();
                                break;

                            default:
                                SkipBlocks();
                                break;
                        }

                        break;

                    case Block.End:
                        // end block - stop!
                        return null;

                    default:
                        throw new Exception( "Unexpected block type" );
                }
            }
        }

        //------------------------------------------------------------------------------

        private void SkipBlocks()
        {
            var blockSize = Input[ D++ ];

            while( blockSize != 0x00 )
            {
                D += blockSize;
                blockSize = Input[ D++ ];
            }
        }

        //------------------------------------------------------------------------------

        private void ReadControlBlock()
        {
            ReadByte(); // block size (0x04)

            var flags    = ReadByte();
            ControlDelay = ReadUInt16();

            // dispose

            switch( (Disposal)( flags & 0x0C ) )
            {
                default:
                case Disposal.None:
                case Disposal.DoNotDispose:
                    PreviousImage = Output;
                    break;

                case Disposal.RestoreBackground:
                    Output = new Color32[ Width * Height ];
                    break;

                case Disposal.ReturnToPrevious:

                    Output = new Color32[ Width * Height ];

                    if( PreviousImage != null )
                    {
                        Array.Copy( PreviousImage, Output, Output.Length );
                    }

                    break;
            }

            // has transparent colour?

            var transparentColour = ReadByte();

            if( ( flags & 0x01 ) == 0x01 )
            {
                TransparentIndex = transparentColour;
            }
            else
            {
                TransparentIndex = NoTransparency;
            }

            ReadByte(); // terminator (0x00)
        }

        //------------------------------------------------------------------------------

        protected Image ReadImageBlock()
        {
            // read image block header

            ImageLeft   = ReadUInt16();
            ImageTop    = ReadUInt16();
            ImageWidth  = ReadUInt16();
            ImageHeight = ReadUInt16();
            var flags   = (ImageFlag) ReadByte();

            // bad image if we don't have any dimensions

            if( ImageWidth == 0 || ImageHeight == 0 )
            {
                return null;
            }

            // read colour table

            if( flags.HasFlag( ImageFlag.ColourTable ) )
            {
                ActiveColourTable = ReadColourTable( LocalColourTable, flags );
            }
            else
            {
                ActiveColourTable = GlobalColourTable;
            }

            if( Output == null )
            {
                Output = new Color32[ Width * Height ];
                PreviousImage = Output;
            }

            // read image data

            DecompressLZW();

            // deinterlace

            if( flags.HasFlag( ImageFlag.Interlaced ) )
            {
                Deinterlace();
            }

            return new Image()
            {
                Width    = Width,
                Height   = Height,
                Delay    = ControlDelay * 10, // (gif are in 1/100th second) convert to ms
                RawImage = Output
            };
        }

        //------------------------------------------------------------------------------
        // decode interlaced images

        protected void Deinterlace()
        {
            var numRows  = Output.Length / Width;
            var writePos = Output.Length - Width; // NB: work backwards due to Y-coord flip
            var input    = Output;

            Output = new Color32[ Output.Length ];

            for( var row = 0; row < numRows; row++ )
            {
                int copyRow;

                // every 8th row starting at 0
                if( row % 8 == 0 )
                {
                    copyRow = row / 8;
                }
                // every 8th row starting at 4
                else if( ( row + 4 ) % 8 == 0 )
                {
                    var o = numRows / 8;
                    copyRow = o + ( row - 4 ) / 8;
                }
                // every 4th row starting at 2
                else if( ( row + 2 ) % 4 == 0 )
                {
                    var o = numRows / 4;
                    copyRow = o + ( row - 2 ) / 4;
                }
                // every 2nd row starting at 1
                else // if( ( r + 1 ) % 2 == 0 )
                {
                    var o = numRows / 2;
                    copyRow = o + ( row - 1 ) / 2;
                }

                Array.Copy( input, ( numRows - copyRow - 1 ) * Width, Output, writePos, Width );

                writePos -= Width;
            }
        }

        //------------------------------------------------------------------------------
        // DecompressLZW()

#if mgGIF_UNSAFE

        bool        Disposed = false;

        int         CodesLength;
        IntPtr      CodesHandle;
        ushort*     pCodes;

        IntPtr      CurBlock;
        uint*       pCurBlock;

        const int   MaxCodes = 4096;
        IntPtr      Indices;
        ushort**    pIndicies;

        public Decoder()
        {
            // unmanaged allocations

            CodesLength = 128 * 1024;
            CodesHandle = Marshal.AllocHGlobal( CodesLength * sizeof( ushort ) );
            pCodes      = (ushort*) CodesHandle.ToPointer();

            CurBlock    = Marshal.AllocHGlobal( 64 * sizeof( uint ) );
            pCurBlock   = (uint*) CurBlock.ToPointer();

            Indices     = Marshal.AllocHGlobal( MaxCodes * sizeof( ushort* ) );
            pIndicies   = (ushort**) Indices.ToPointer();
        }

        protected virtual void Dispose( bool disposing )
        {
            if( Disposed )
            {
                return;
            }

            // release unmanaged resources

            Marshal.FreeHGlobal( CodesHandle );
            Marshal.FreeHGlobal( CurBlock );
            Marshal.FreeHGlobal( Indices );
            
            Disposed = true;
        }

        ~Decoder()
        {
            Dispose( false );
        }

        public void Dispose()
        {
            Dispose( true );
            GC.SuppressFinalize( this );
        }

        private void DecompressLZW()
        {
            var pCodeBufferEnd = pCodes + CodesLength;

            fixed( byte* pData = Input )
            {
                fixed( Color32* pOutput = Output, pColourTable = ActiveColourTable )
                {
                    var row       = ( Height - ImageTop - 1 ) * Width; // start at end of array as we are reversing the row order
                    var safeWidth = ImageLeft + ImageWidth > Width ? Width - ImageLeft : ImageWidth;

                    var pWrite    = &pOutput[ row + ImageLeft ];
                    var pRow      = pWrite;
                    var pRowEnd   = pWrite + ImageWidth;
                    var pImageEnd = pWrite + safeWidth;

                    // setup codes

                    int minimumCodeSize = Input[ D++ ];

                    if( minimumCodeSize > 11 )
                    {
                        minimumCodeSize = 11;
                    }

                    var codeSize        = minimumCodeSize + 1;
                    var nextSize        = Pow2[ codeSize ];
                    var maximumCodeSize = Pow2[ minimumCodeSize ];
                    var clearCode       = maximumCodeSize;
                    var endCode         = maximumCodeSize + 1;

                    // initialise buffers

                    var numCodes  = maximumCodeSize + 2;
                    var pCodesEnd = pCodes;

                    for( ushort i = 0; i < numCodes; i++ )
                    {
                        pIndicies[ i ] = pCodesEnd;
                        *pCodesEnd++ = 1;
                        *pCodesEnd++ = i;
                    }

                    // LZW decode loop

                    uint previousCode   = NoCode;   // last code processed
                    uint mask           = (uint) ( nextSize - 1 ); // mask out code bits
                    uint shiftRegister  = 0;        // shift register holds the bytes coming in from the input stream, we shift down by the number of bits

                    int  bitsAvailable  = 0;        // number of bits available to read in the shift register
                    int  bytesAvailable = 0;        // number of bytes left in current block

                    uint* pD = pCurBlock;           // pointer to next bits in current block

                    while( true )
                    {
                        // get next code

                        uint curCode = shiftRegister & mask;

                        if( bitsAvailable >= codeSize )
                        {
                            // we had enough bits in the shift register so shunt it down
                            bitsAvailable -= codeSize;
                            shiftRegister >>= codeSize;
                        }
                        else
                        {
                            // not enough bits in register, so get more

                            // if start of new block

                            if( bytesAvailable <= 0 )
                            {
                                // read blocksize

                                var pBlock = &pData[ D++ ];
                                bytesAvailable = *pBlock++;
                                D += bytesAvailable;

                                // exit if end of stream

                                if( bytesAvailable == 0 )
                                {
                                    return;
                                }

                                // copy block into buffer

                                pCurBlock[ ( bytesAvailable - 1 ) / 4 ] = 0; // zero last entry
                                Buffer.MemoryCopy( pBlock, pCurBlock, 256, bytesAvailable );
                                pD = pCurBlock;
                            }

                            // load shift register

                            shiftRegister = *pD++;
                            int newBits = bytesAvailable >= 4 ? 32 : bytesAvailable * 8;
                            bytesAvailable -= 4;

                            // read remaining bits

                            if( bitsAvailable > 0 )
                            {
                                var bitsRemaining = codeSize - bitsAvailable;
                                curCode |= ( shiftRegister << bitsAvailable ) & mask;
                                shiftRegister >>= bitsRemaining;
                                bitsAvailable = newBits - bitsRemaining;
                            }
                            else
                            {
                                curCode = shiftRegister & mask;
                                shiftRegister >>= codeSize;
                                bitsAvailable = newBits - codeSize;
                            }
                        }

                        // process code

                        if( curCode == clearCode )
                        {
                            // reset codes
                            codeSize = minimumCodeSize + 1;
                            nextSize = Pow2[ codeSize ];
                            numCodes = maximumCodeSize + 2;

                            // reset buffer write pos
                            pCodesEnd = &pCodes[ numCodes * 2 ];

                            // clear previous code
                            previousCode = NoCode;
                            mask = (uint)( nextSize - 1 );

                            continue;
                        }
                        else if( curCode == endCode )
                        {
                            // stop
                            break;
                        }

                        bool plusOne = false;
                        ushort* pCodePos = null;

                        if( curCode < numCodes )
                        {
                            // write existing code
                            pCodePos = pIndicies[ curCode ];
                        }
                        else if( previousCode != NoCode )
                        {
                            // write previous code
                            pCodePos = pIndicies[ previousCode ];
                            plusOne = true;
                        }
                        else
                        {
                            continue;
                        }


                        // output colours

                        var codeLength = *pCodePos++;
                        var newCode    = *pCodePos;
                        var pEnd       = pCodePos + codeLength;

                        do
                        {
                            var code = *pCodePos++;

                            if( code != TransparentIndex && pWrite < pImageEnd )
                            {
                                *pWrite = pColourTable[ code ];
                            }

                            if( ++pWrite == pRowEnd )
                            {
                                pRow -= Width;
                                pWrite    = pRow;
                                pRowEnd   = pRow + ImageWidth;
                                pImageEnd = pRow + safeWidth;

                                if( pWrite < pOutput )
                                {
                                    goto Exit;
                                }
                            }
                        }
                        while( pCodePos < pEnd );

                        if( plusOne )
                        {
                            if( newCode != TransparentIndex && pWrite < pImageEnd )
                            {
                                *pWrite = pColourTable[ newCode ];
                            }

                            if( ++pWrite == pRowEnd )
                            {
                                pRow -= Width;
                                pWrite    = pRow;
                                pRowEnd   = pRow + ImageWidth;
                                pImageEnd = pRow + safeWidth;

                                if( pWrite < pOutput )
                                {
                                    goto Exit;
                                }
                            }
                        }

                        // create new code

                        if( previousCode != NoCode && numCodes != MaxCodes )
                        {
                            // get previous code from buffer

                            pCodePos = pIndicies[ previousCode ];
                            codeLength = *pCodePos++;

                            // resize buffer if required (should be rare)

                            if( pCodesEnd + codeLength + 1 >= pCodeBufferEnd )
                            {
                                var pBase = pCodes;

                                // realloc buffer
                                CodesLength *= 2;
                                CodesHandle = Marshal.ReAllocHGlobal( CodesHandle, (IntPtr)( CodesLength * sizeof( ushort ) ) );

                                pCodes         = (ushort*) CodesHandle.ToPointer();
                                pCodeBufferEnd = pCodes + CodesLength;

                                // rebase pointers
                                pCodesEnd = pCodes + ( pCodesEnd - pBase );

                                for( int i=0; i < numCodes; i++ )
                                {
                                    pIndicies[ i ] = pCodes + ( pIndicies[ i ] - pBase );
                                }

                                pCodePos = pIndicies[ previousCode ];
                                pCodePos++;

                            }

                            // add new code

                            pIndicies[ numCodes++ ] = pCodesEnd;
                            *pCodesEnd++ = (ushort)( codeLength + 1 );

                            // copy previous code sequence

                            Buffer.MemoryCopy( pCodePos, pCodesEnd, codeLength * sizeof( ushort ), codeLength * sizeof( ushort ) );
                            pCodesEnd += codeLength;

                            // append new code

                            *pCodesEnd++ = newCode;
                        }

                        // increase code size?

                        if( numCodes >= nextSize && codeSize < 12 )
                        {
                            nextSize = Pow2[ ++codeSize ];
                            mask     = (uint)( nextSize - 1 );
                        }

                        // remember last code processed
                        previousCode = curCode;
                    }

                Exit:

                    // consume any remaining blocks
                    SkipBlocks();
                }
            }
        }

#else

        int[]    codeIndex = new int[ 4098 ];             // codes can be upto 12 bytes long, this is the maximum number of possible codes (2^12 + 2 for clear and end code)
        ushort[] codes     = new ushort[ 128 * 1024 ];    // 128k buffer for codes - should be plenty but we dynamically resize if required

        private Color32[] DecompressLZW()
        {
            // output write position

            var output = new Color32[ Width * Height ];

            if( ControlDispose != Disposal.RestoreBackground && PrevImage != null )
            {
                Array.Copy( PrevImage, output, PrevImage.Length );
            }

            int row       = ( Height - ImageTop - 1 ) * Width; // reverse rows for unity texture coords
            int col       = ImageLeft;
            int rightEdge = ImageLeft + ImageWidth;

            // setup codes

            int minimumCodeSize = Data[ D++ ];

            if( minimumCodeSize > 11 )
            {
                minimumCodeSize = 11;
            }

            var codeSize        = minimumCodeSize + 1;
            var nextSize        = Pow2[ codeSize ];
            var maximumCodeSize = Pow2[ minimumCodeSize ];
            var clearCode       = maximumCodeSize;
            var endCode         = maximumCodeSize + 1;

            // initialise buffers

            var codesEnd = 0;
            var numCodes = maximumCodeSize + 2;

            for( ushort i = 0; i < numCodes; i++ )
            {
                codeIndex[ i ] = codesEnd;
                codes[ codesEnd++ ] = 1; // length
                codes[ codesEnd++ ] = i; // code
            }

            // LZW decode loop

            uint previousCode   = NoCode; // last code processed
            uint mask           = (uint) ( nextSize - 1 ); // mask out code bits
            uint shiftRegister  = 0; // shift register holds the bytes coming in from the input stream, we shift down by the number of bits

            int  bitsAvailable  = 0; // number of bits available to read in the shift register
            int  bytesAvailable = 0; // number of bytes left in current block

            while( true )
            {
                // get next code

                uint curCode = shiftRegister & mask;

                if( bitsAvailable >= codeSize )
                {
                    bitsAvailable -= codeSize;
                    shiftRegister >>= codeSize;
                }
                else
                {
                    // reload shift register


                    // if start of new block
                    if( bytesAvailable == 0 )
                    {
                        // read blocksize
                        bytesAvailable = Data[ D++ ];

                        // exit if end of stream
                        if( bytesAvailable == 0 )
                        {
                            return output;
                        }
                    }


                    int newBits = 32;

                    if( bytesAvailable >= 4 )
                    {
                        shiftRegister = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 | Data[ D++ ] << 16 | Data[ D++ ] << 24 );
                        bytesAvailable -= 4;
                    }
                    else if( bytesAvailable == 3 )
                    {
                        shiftRegister = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 | Data[ D++ ] << 16 );
                        bytesAvailable = 0;
                        newBits = 24;
                    }
                    else if( bytesAvailable == 2 )
                    {
                        shiftRegister = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 );
                        bytesAvailable = 0;
                        newBits = 16;
                    }
                    else
                    {
                        shiftRegister = Data[ D++ ];
                        bytesAvailable = 0;
                        newBits = 8;
                    }

                    if( bitsAvailable > 0 )
                    {
                        var bitsRemaining = codeSize - bitsAvailable;
                        curCode |= ( shiftRegister << bitsAvailable ) & mask;
                        shiftRegister >>= bitsRemaining;
                        bitsAvailable = newBits - bitsRemaining;
                    }
                    else
                    {
                        curCode = shiftRegister & mask;
                        shiftRegister >>= codeSize;
                        bitsAvailable = newBits - codeSize;
                    }
                }

                // process code

                bool plusOne = false;
                int  codePos = 0;

                if( curCode == clearCode )
                {
                    // reset codes
                    codeSize = minimumCodeSize + 1;
                    nextSize = Pow2[ codeSize ];
                    numCodes = maximumCodeSize + 2;

                    // reset buffer write pos
                    codesEnd = numCodes * 2;

                    // clear previous code
                    previousCode = NoCode;
                    mask = (uint) ( nextSize - 1 );

                    continue;
                }
                else if( curCode == endCode )
                {
                    // stop
                    break;
                }
                else if( curCode < numCodes )
                {
                    // write existing code
                    codePos = codeIndex[ curCode ];
                }
                else if( previousCode != NoCode )
                {
                    // write previous code
                    codePos = codeIndex[ previousCode ];
                    plusOne = true;
                }
                else
                {
                    continue;
                }


                // output colours

                var codeLength = codes[ codePos++ ];
                var newCode    = codes[ codePos ];

                for( int i = 0; i < codeLength; i++ )
                {
                    var code = codes[ codePos++ ];

                    if( code != TransparentIndex && col < Width )
                    {
                        output[ row + col ] = ActiveColourTable[ code ];
                    }

                    if( ++col == rightEdge )
                    {
                        col = ImageLeft;
                        row -= Width;

                        if( row < 0 )
                        {
                            goto Exit;
                        }
                    }
                }

                if( plusOne )
                {
                    if( newCode != TransparentIndex && col < Width )
                    {
                        output[ row + col ] = ActiveColourTable[ newCode ];
                    }

                    if( ++col == rightEdge )
                    {
                        col = ImageLeft;
                        row -= Width;

                        if( row < 0 )
                        {
                            goto Exit;
                        }
                    }
                }

                // create new code

                if( previousCode != NoCode && numCodes != codeIndex.Length )
                {
                    // get previous code from buffer

                    codePos = codeIndex[ previousCode ];
                    codeLength = codes[ codePos++ ];

                    // resize buffer if required (should be rare)

                    if( codesEnd + codeLength + 1 >= codes.Length )
                    {
                        Array.Resize( ref codes, codes.Length * 2 );
                    }

                    // add new code

                    codeIndex[ numCodes++ ] = codesEnd;
                    codes[ codesEnd++ ] = (ushort) ( codeLength + 1 );

                    // copy previous code sequence

                    var stop = codesEnd + codeLength;

                    while( codesEnd < stop )
                    {
                        codes[ codesEnd++ ] = codes[ codePos++ ];
                    }

                    // append new code

                    codes[ codesEnd++ ] = newCode;
                }

                // increase code size?

                if( numCodes >= nextSize && codeSize < 12 )
                {
                    nextSize = Pow2[ ++codeSize ];
                    mask = (uint) ( nextSize - 1 );
                }

                // remember last code processed
                previousCode = curCode;
            }

        Exit:

            // skip any remaining bytes
            D += bytesAvailable;

            // consume any remaining blocks
            SkipBlocks();

            return output;
        }
#endif // mgGIF_UNSAFE
    }
}

