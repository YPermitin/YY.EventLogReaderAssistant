﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using YY.EventLogReaderAssistant.EventArguments;
using YY.EventLogReaderAssistant.Helpers;
using YY.EventLogReaderAssistant.Models;

[assembly: InternalsVisibleTo("YY.EventLogReaderAssistant.Tests")]
namespace YY.EventLogReaderAssistant
{
    internal sealed class EventLogLGFReader : EventLogReader
    {
        #region Private Member Variables

        private const long DefaultBeginLineForLgf = 3;
        private int _indexCurrentFile;
        private readonly string[] _logFilesWithData;
        private long _eventCount = -1;

        StreamReader _stream;
        readonly StringBuilder _eventSource;

        private LogParserLGF _logParser;
        private LogParserLGF LogParser
        {
            get
            {
                if (_logParser == null)
                    _logParser = new LogParserLGF(this);

                return _logParser;
            }
        }

        #endregion

        #region Public Properties

        public override string CurrentFile
        {
            get
            {
                if (_logFilesWithData.Length <= _indexCurrentFile)
                    return null;
                else
                    return _logFilesWithData[_indexCurrentFile];
            }
        }

        #endregion

        #region Constructor

        internal EventLogLGFReader(string logFilePath) : base(logFilePath)
        {
            _indexCurrentFile = 0;
            _logFilesWithData = Directory
                .GetFiles(_logFileDirectoryPath, "*.lgp")
                .OrderBy(i => i)
                .ToArray();
            _eventSource = new StringBuilder();            
        }

        #endregion

        #region Public Methods

        public override bool Read()
        {
            try
            {
                if (_stream == null)
                {
                    if (_logFilesWithData.Length <= _indexCurrentFile)
                    {
                        _currentRow = null;
                        return false;
                    }
                    
                    InitializeStream(DefaultBeginLineForLgf, _indexCurrentFile);
                    _currentFileEventNumber = 0;
                }
                _eventSource.Clear();

                BeforeReadFileEventArgs beforeReadFileArgs = new BeforeReadFileEventArgs(CurrentFile);
                if (_currentFileEventNumber == 0)
                    RaiseBeforeReadFile(beforeReadFileArgs);

                if (beforeReadFileArgs.Cancel)
                {
                    NextFile();
                    return Read();
                }

                string sourceData;
                bool newLine = true;
                int countBracket = 0;
                bool textBlockOpen = false;

                while (true)
                {
                    sourceData = _stream.ReadLine();

                    if(sourceData == "," && NextLineIsBeginEvent())
                        sourceData = _stream.ReadLine();

                    if (sourceData == null)
                    {
                        NextFile();
                        return Read();
                    }

                    if (newLine)
                    {
                        _eventSource.Append(sourceData);
                    }
                    else
                    {
                        _eventSource.AppendLine();
                        _eventSource.Append(sourceData);
                    }

                    if (LogParserLGF.ItsEndOfEvent(sourceData, ref countBracket, ref textBlockOpen))
                    {
                        _currentFileEventNumber += 1;
                        string prepearedSourceData = _eventSource.ToString();

                        RaiseBeforeRead(new BeforeReadEventArgs(prepearedSourceData, _currentFileEventNumber));

                        try
                        {
                            RowData eventData = LogParser.Parse(prepearedSourceData);
                            if (eventData != null)
                            {
                                if (eventData.Period >= ReferencesReadDate)
                                {
                                    ReadEventLogReferences();
                                    eventData = LogParser.Parse(prepearedSourceData);
                                }
                            }

                            if (Math.Abs(_readDelayMilliseconds) > 0 && eventData != null)
                            {
                                DateTimeOffset stopPeriod = DateTimeOffset.Now.AddMilliseconds(-_readDelayMilliseconds);
                                if (eventData.Period >= stopPeriod)
                                {
                                    _currentRow = null;
                                    return false;
                                }
                            }

                            _currentRow = eventData;

                            RaiseAfterRead(new AfterReadEventArgs(_currentRow, _currentFileEventNumber));
                            return true;
                        }
                        catch (Exception ex)
                        {
                            RaiseOnError(new OnErrorEventArgs(ex, prepearedSourceData, false));
                            _currentRow = null;
                            return true;
                        }
                    }
                    else
                    {
                        newLine = false;
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseOnError(new OnErrorEventArgs(ex, null, true));
                _currentRow = null;
                return false;
            }
        }
        public override bool GoToEvent(long eventNumber)
        {
            Reset();

            int fileIndex = -1;
            long currentLineNumber = -1;
            long currentEventNumber = 0;
            bool moved = false;

            foreach (string logFile in _logFilesWithData)
            {
                fileIndex += 1;
                currentLineNumber = -1;

                IEnumerable<string> allLines = File.ReadLines(logFile);
                foreach (string line in allLines)
                {
                    currentLineNumber += 1;
                    if(LogParserLGF.ItsBeginOfEvent(line))                    
                    {
                        currentEventNumber += 1;
                    }

                    if (currentEventNumber == eventNumber)
                    {
                        moved = true;
                        break;
                    }
                }

                if (currentEventNumber == eventNumber)
                {
                    moved = true;
                    break;
                }
            }           

            if (moved && fileIndex >= 0 && currentLineNumber >= 0)
            {
                InitializeStream(currentLineNumber, fileIndex);
                _eventCount = eventNumber - 1;
                _currentFileEventNumber = eventNumber;

                return true;
            }
            else
            {
                return false;
            }
        }
        public override EventLogPosition GetCurrentPosition()
        {
            return new EventLogPosition(
                _currentFileEventNumber, 
                _logFilePath, 
                CurrentFile, 
                GetCurrentFileStreamPosition());
        }
        public override void SetCurrentPosition(EventLogPosition newPosition)
        {
            Reset();
            if (newPosition == null)
                return;

            if(newPosition.CurrentFileReferences != _logFilePath)
                throw new Exception("Invalid data file with references");

            int indexOfFileData = Array.IndexOf(_logFilesWithData, newPosition.CurrentFileData);
            if (indexOfFileData < 0)
                throw new Exception("Invalid data file");
            _indexCurrentFile = indexOfFileData;

            _currentFileEventNumber = newPosition.EventNumber;

            InitializeStream(DefaultBeginLineForLgf, _indexCurrentFile);
            long beginReadPosition =_stream.GetPosition();

            long newStreamPosition;
            if (newPosition.StreamPosition == null)
                newStreamPosition = 0;
            else
                newStreamPosition = (long)newPosition.StreamPosition;

            if(newStreamPosition < beginReadPosition)            
                newStreamPosition = beginReadPosition;

            long sourceStreamPosition = newStreamPosition;
            string currentFilePath = _logFilesWithData[_indexCurrentFile];            
            
            bool isCorrectBeginEvent = false;
            bool notDataAvailiable = false;

            FixBeginEventPosition(
                ref isCorrectBeginEvent,
                currentFilePath,
                ref newStreamPosition, 
                ref notDataAvailiable);

            if (!isCorrectBeginEvent && !notDataAvailiable)
            {
                newStreamPosition = sourceStreamPosition;
                FixBeginEventPosition(
                    ref isCorrectBeginEvent,
                    currentFilePath,
                    ref newStreamPosition,
                    ref notDataAvailiable,
                    -1);
            }

            if (!isCorrectBeginEvent && !notDataAvailiable)
            {
                throw new ArgumentException("Wrong begin event stream position's");
            }

            if (newPosition.StreamPosition != null)
                SetCurrentFileStreamPosition(newStreamPosition);
        }
        public override long Count()
        {
            if(_eventCount < 0)
                _eventCount = GetEventCount();

            return _eventCount;
        }
        public override void Reset()
        {
            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }

            _indexCurrentFile = 0;
            _currentFileEventNumber = 0;
            _currentRow = null;
        }
        public override void NextFile()
        {
            RaiseAfterReadFile(new AfterReadFileEventArgs(CurrentFile));

            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }

            _indexCurrentFile += 1;
        }
        public override void Dispose()
        {
            base.Dispose();

            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }
        }
        protected override void ReadEventLogReferences()
        {
            DateTime beginReadReferences = DateTime.Now;

            var referencesInfo = LogParser.GetEventLogReferences();
            referencesInfo.ReadReferencesByType(_users);
            referencesInfo.ReadReferencesByType(_computers);
            referencesInfo.ReadReferencesByType(_applications);
            referencesInfo.ReadReferencesByType(_events);
            referencesInfo.ReadReferencesByType(_metadata);
            referencesInfo.ReadReferencesByType(_workServers);
            referencesInfo.ReadReferencesByType(_primaryPorts);
            referencesInfo.ReadReferencesByType(_secondaryPorts);

            _referencesReadDate = beginReadReferences;

            base.ReadEventLogReferences();
        }
        public long GetCurrentFileStreamPosition()
        {
            if (_stream != null)
                return _stream.GetPosition();
            else
                return 0;
        }
        public void SetCurrentFileStreamPosition(long position)
        {
            if (_stream != null)
                _stream.SetPosition(position);
        }

        #endregion

        #region Private Methods

        private void InitializeStream(long linesToSkip, int fileIndex = 0)
        {
            FileStream fs = new FileStream(_logFilesWithData[fileIndex], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _stream = new StreamReader(fs);
            _stream.SkipLine(linesToSkip);
        }
        private long GetEventCount()
        {
            long eventCount = 0;

            foreach (var logFile in _logFilesWithData)
            {
                using (StreamReader logFileStream = new StreamReader(File.Open(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    do
                    {
                        string logFileCurrentString = logFileStream.ReadLine();
                        if(LogParserLGF.ItsBeginOfEvent(logFileCurrentString))
                            eventCount++;
                    } while (!logFileStream.EndOfStream);
                }
            }

            return eventCount;
        }
        private bool NextLineIsBeginEvent()
        {
            if (CurrentFile == null || _stream == null)
                return false;

            bool nextIsBeginEvent;
            long currentStreamPosition = _stream.GetPosition();

            using (FileStream fileStreamCheckReader = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (StreamReader checkReader = new StreamReader(fileStreamCheckReader))
                {
                    checkReader.SetPosition(currentStreamPosition);
                    string lineContent = checkReader.ReadLine();
                    nextIsBeginEvent = LogParserLGF.ItsBeginOfEvent(lineContent);
                }
            }            

            return nextIsBeginEvent;
        }
        private void FixBeginEventPosition(ref bool isCorrectBeginEvent, string currentFilePath, ref long newStreamPosition, ref bool notDataAvailiable, int stepSize = 1)
        {
            int attemptToFoundBeginEventLine = 0;
            while (!isCorrectBeginEvent && attemptToFoundBeginEventLine < 10)
            {
                string beginEventLine;
                using (FileStream fileStreamCheckPosition = new FileStream(currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fileStreamCheckPosition.Seek(newStreamPosition, SeekOrigin.Begin);
                    using (StreamReader fileStreamCheckReader = new StreamReader(fileStreamCheckPosition))
                        beginEventLine = fileStreamCheckReader.ReadLine();
                }

                if (beginEventLine == null)
                {
                    notDataAvailiable = true;
                    break;
                }

                isCorrectBeginEvent = LogParserLGF.ItsBeginOfEvent(beginEventLine);
                if (!isCorrectBeginEvent)
                {
                    newStreamPosition -= stepSize;
                    attemptToFoundBeginEventLine += 1;
                }
            }
        }
        #endregion
    }
}
