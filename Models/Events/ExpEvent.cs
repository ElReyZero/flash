﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace watchtower.Models.Events {

    public class ExpEvent {

        public string SourceID { get; set; } = "";

        public DateTime Timestamp { get; set; }

        public int LoadoutID { get; set; } 

        public int ZoneID { get; set; }

        public string TargetID { get; set; } = "";

        public int ExpID { get; set; }

        public int Amount { get; set; }

        public int WorldID { get; set; }

    }
}
