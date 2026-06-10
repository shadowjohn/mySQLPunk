using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace mySQLPunk
{
    public static class GeometryWktConverter
    {
        private const uint EwkbZ = 0x80000000;
        private const uint EwkbM = 0x40000000;
        private const uint EwkbSrid = 0x20000000;
        private const uint EwkbTypeMask = 0x000000FF;

        public static bool TryGeometryBytesToWkt(byte[] bytes, out string wkt)
        {
            wkt = "";
            if (bytes == null || bytes.Length < 5) return false;

            if (TryReadSpatiaLiteBlob(bytes, out wkt)) return true;

            int[] preferredOffsets = { 0, 4, 39 };
            foreach (int offset in preferredOffsets)
            {
                if (TryReadWkbAt(bytes, offset, out wkt)) return true;
            }

            int scanLimit = Math.Min(bytes.Length - 5, 80);
            for (int offset = 0; offset <= scanLimit; offset++)
            {
                if (TryReadWkbAt(bytes, offset, out wkt)) return true;
            }

            return false;
        }

        private static bool TryReadSpatiaLiteBlob(byte[] bytes, out string wkt)
        {
            wkt = "";
            try
            {
                if (bytes.Length < 44) return false;
                byte endian = bytes[1];
                if (bytes[0] != 0 || (endian != 0 && endian != 1) || bytes[38] != 0x7c || bytes[bytes.Length - 1] != 0xfe) return false;

                WkbReader reader = new WkbReader(bytes, 39, endian == 1, true);
                string parsed = reader.ReadSpatiaLiteGeometry();
                if (string.IsNullOrWhiteSpace(parsed)) return false;
                wkt = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadWkbAt(byte[] bytes, int offset, out string wkt)
        {
            wkt = "";
            try
            {
                WkbReader reader = new WkbReader(bytes, offset);
                string parsed = reader.ReadGeometry();
                if (string.IsNullOrWhiteSpace(parsed)) return false;
                wkt = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class WkbReader
        {
            private readonly byte[] bytes;
            private int position;
            private bool littleEndian;
            private bool hasZ;
            private bool hasM;
            private readonly bool spatiaLiteMode;

            public WkbReader(byte[] source, int offset)
            {
                bytes = source ?? throw new ArgumentNullException(nameof(source));
                position = offset;
            }

            public WkbReader(byte[] source, int offset, bool littleEndian, bool spatiaLiteMode)
                : this(source, offset)
            {
                this.littleEndian = littleEndian;
                this.spatiaLiteMode = spatiaLiteMode;
            }

            public string ReadGeometry()
            {
                Ensure(5);
                byte endian = ReadByte();
                if (endian != 0 && endian != 1) throw new FormatException(Localization.T("Geometry.InvalidWkbByteOrder"));
                littleEndian = endian == 1;

                uint rawType = ReadUInt32();
                return ReadGeometryFromType(rawType);
            }

            public string ReadSpatiaLiteGeometry()
            {
                Ensure(4);
                uint rawType = ReadUInt32();
                return ReadGeometryFromType(rawType);
            }

            private string ReadSpatiaLiteEntity()
            {
                byte marker = ReadByte();
                if (marker != 0x69) throw new FormatException(Localization.T("Geometry.InvalidSpatiaLiteMarker"));

                uint rawType = ReadUInt32();
                return ReadGeometryFromType(rawType);
            }

            private string ReadGeometryFromType(uint rawType)
            {
                bool ewkbHasZ = (rawType & EwkbZ) != 0;
                bool ewkbHasM = (rawType & EwkbM) != 0;
                bool ewkbHasSrid = (rawType & EwkbSrid) != 0;
                bool hasEwkbFlags = ewkbHasZ || ewkbHasM || ewkbHasSrid;
                uint type = hasEwkbFlags ? rawType & EwkbTypeMask : rawType;

                if (!hasEwkbFlags && rawType >= 1000000) throw new FormatException(Localization.T("Geometry.CompressedSpatiaLiteUnsupported"));

                if (!hasEwkbFlags && rawType >= 3000 && rawType < 4000)
                {
                    type = rawType - 3000;
                    ewkbHasZ = true;
                    ewkbHasM = true;
                }
                else if (!hasEwkbFlags && rawType >= 2000 && rawType < 3000)
                {
                    type = rawType - 2000;
                    ewkbHasM = true;
                }
                else if (!hasEwkbFlags && rawType >= 1000 && rawType < 2000)
                {
                    type = rawType - 1000;
                    ewkbHasZ = true;
                }

                if (type < 1 || type > 7) throw new FormatException(Localization.T("Geometry.UnsupportedWkbType"));
                hasZ = ewkbHasZ;
                hasM = ewkbHasM;
                if (ewkbHasSrid) ReadUInt32();

                return ReadGeometryByType(type);
            }

            private string ReadGeometryByType(uint type)
            {
                switch (type)
                {
                    case 1:
                        return "POINT " + FormatPointBody(ReadPoint());
                    case 2:
                        return "LINESTRING " + FormatPointList(ReadPointList());
                    case 3:
                        return "POLYGON " + FormatPolygon(ReadPolygonRings());
                    case 4:
                        return "MULTIPOINT " + FormatMultiPoint(ReadGeometryList("POINT"));
                    case 5:
                        return "MULTILINESTRING " + FormatMultiLineString(ReadGeometryList("LINESTRING"));
                    case 6:
                        return "MULTIPOLYGON " + FormatMultiPolygon(ReadGeometryList("POLYGON"));
                    case 7:
                        return "GEOMETRYCOLLECTION " + FormatGeometryCollection(ReadGeometryList(null));
                    default:
                        throw new FormatException(Localization.T("Geometry.UnsupportedWkbType"));
                }
            }

            private List<string> ReadGeometryList(string expectedPrefix)
            {
                uint count = ReadUInt32();
                if (count > 100000) throw new FormatException(Localization.T("Geometry.CollectionTooLarge"));

                List<string> items = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    if (spatiaLiteMode)
                    {
                        string entity = ReadSpatiaLiteEntity();
                        if (!string.IsNullOrWhiteSpace(expectedPrefix) &&
                            !entity.StartsWith(expectedPrefix + " ", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FormatException(Localization.T("Geometry.UnexpectedNestedType"));
                        }
                        items.Add(entity);
                        continue;
                    }

                    WkbReader nested = new WkbReader(bytes, position);
                    string child = nested.ReadGeometry();
                    position = nested.position;
                    if (!string.IsNullOrWhiteSpace(expectedPrefix) &&
                        !child.StartsWith(expectedPrefix + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException(Localization.T("Geometry.UnexpectedNestedType"));
                    }
                    items.Add(child);
                }
                return items;
            }

            private Coordinate ReadPoint()
            {
                double x = ReadDouble();
                double y = ReadDouble();
                double z = hasZ ? ReadDouble() : double.NaN;
                if (hasM) ReadDouble();
                return new Coordinate(x, y, z);
            }

            private List<Coordinate> ReadPointList()
            {
                uint count = ReadUInt32();
                if (count > 1000000) throw new FormatException(Localization.T("Geometry.PointListTooLarge"));

                List<Coordinate> points = new List<Coordinate>();
                for (uint i = 0; i < count; i++) points.Add(ReadPoint());
                return points;
            }

            private List<List<Coordinate>> ReadPolygonRings()
            {
                uint ringCount = ReadUInt32();
                if (ringCount > 100000) throw new FormatException(Localization.T("Geometry.PolygonTooManyRings"));

                List<List<Coordinate>> rings = new List<List<Coordinate>>();
                for (uint i = 0; i < ringCount; i++) rings.Add(ReadPointList());
                return rings;
            }

            private byte ReadByte()
            {
                Ensure(1);
                return bytes[position++];
            }

            private uint ReadUInt32()
            {
                Ensure(4);
                uint value = littleEndian
                    ? (uint)(bytes[position] | (bytes[position + 1] << 8) | (bytes[position + 2] << 16) | (bytes[position + 3] << 24))
                    : (uint)((bytes[position] << 24) | (bytes[position + 1] << 16) | (bytes[position + 2] << 8) | bytes[position + 3]);
                position += 4;
                return value;
            }

            private double ReadDouble()
            {
                Ensure(8);
                byte[] buffer = new byte[8];
                Array.Copy(bytes, position, buffer, 0, 8);
                if (BitConverter.IsLittleEndian != littleEndian) Array.Reverse(buffer);
                position += 8;
                return BitConverter.ToDouble(buffer, 0);
            }

            private void Ensure(int count)
            {
                if (position < 0 || position + count > bytes.Length) throw new FormatException(Localization.T("Geometry.UnexpectedEndOfWkb"));
            }

            private static string FormatPointBody(Coordinate point)
            {
                return "(" + FormatCoordinate(point) + ")";
            }

            private static string FormatPointList(List<Coordinate> points)
            {
                List<string> output = new List<string>();
                foreach (Coordinate point in points) output.Add(FormatCoordinate(point));
                return "(" + string.Join(", ", output.ToArray()) + ")";
            }

            private static string FormatPolygon(List<List<Coordinate>> rings)
            {
                List<string> output = new List<string>();
                foreach (List<Coordinate> ring in rings) output.Add(FormatPointList(ring));
                return "(" + string.Join(", ", output.ToArray()) + ")";
            }

            private static string FormatMultiPoint(List<string> points)
            {
                List<string> output = new List<string>();
                foreach (string point in points) output.Add(StripPrefix(point, "POINT "));
                return "(" + string.Join(", ", output.ToArray()) + ")";
            }

            private static string FormatMultiLineString(List<string> lines)
            {
                List<string> output = new List<string>();
                foreach (string line in lines) output.Add(StripPrefix(line, "LINESTRING "));
                return "(" + string.Join(", ", output.ToArray()) + ")";
            }

            private static string FormatMultiPolygon(List<string> polygons)
            {
                List<string> output = new List<string>();
                foreach (string polygon in polygons) output.Add(StripPrefix(polygon, "POLYGON "));
                return "(" + string.Join(", ", output.ToArray()) + ")";
            }

            private static string FormatGeometryCollection(List<string> geometries)
            {
                return "(" + string.Join(", ", geometries.ToArray()) + ")";
            }

            private static string StripPrefix(string value, string prefix)
            {
                return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? value.Substring(prefix.Length)
                    : value;
            }

            private static string FormatCoordinate(Coordinate coordinate)
            {
                if (double.IsNaN(coordinate.Z))
                {
                    return FormatNumber(coordinate.X) + " " + FormatNumber(coordinate.Y);
                }
                return FormatNumber(coordinate.X) + " " + FormatNumber(coordinate.Y) + " " + FormatNumber(coordinate.Z);
            }

            private static string FormatNumber(double value)
            {
                return value.ToString("G17", CultureInfo.InvariantCulture);
            }
        }

        private struct Coordinate
        {
            public readonly double X;
            public readonly double Y;
            public readonly double Z;

            public Coordinate(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
    }
}
