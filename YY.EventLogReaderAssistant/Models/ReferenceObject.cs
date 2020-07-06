﻿using System;
using System.Data.SQLite;
using YY.EventLogReaderAssistant.Helpers;

namespace YY.EventLogReaderAssistant.Models
{
    [Serializable]
    public abstract class ReferenceObject : IReferenceObject
    {
        #region Public Members

        public long Code { get; set; }
        public string Name { get; set; }

        #endregion

        #region Public Methods

        public virtual void FillBySqliteReader(SQLiteDataReader reader)
        {
            Code = reader.GetInt64(0);
            Name = reader.GetString(1);
        }
        public virtual void FillByStringParsedData(string[] parsedEventData)
        {
            Code = parsedEventData[2].ToInt64();
            Name = parsedEventData[1];
        }
        public override string ToString()
        {
            return Name;
        }

        #endregion
    }
}
